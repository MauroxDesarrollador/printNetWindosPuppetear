using PuppeteerSharp;
using PuppeteerSharp.Media;
using PdfiumViewer;
using System.Drawing.Printing;
using System.Drawing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Configure CORS to allow any origin (replicating Node.js behavior)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");

app.MapPost("/print", async (PrintRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest("No URL provided. Use { \"url\": \"https://...\" }");
    }

    Console.WriteLine($"Received print request for URL: {request.Url}");
    string outputPdfPath = Path.Combine(Directory.GetCurrentDirectory(), $"output_{DateTime.Now.Ticks}.pdf");

    try
    {
        // 1. Download Browser (if needed) - typically done once, but ensuring it's available
        // Optimally this should be done at startup, but for simplicity keeping it here or cached.
        // For production, consider moving BrowserFetcher to app startup.
        using var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        // 2. Launch Browser
        var launchOptions = new LaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
        };

        using var browser = await Puppeteer.LaunchAsync(launchOptions);
        using var page = await browser.NewPageAsync();
        
        // Viewport
        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 800 });

        // Navigate
        var navOptions = new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }, Timeout = 30000 };
        await page.GoToAsync(request.Url, navOptions);

        // Wait for selector if provided
        if (!string.IsNullOrEmpty(request.WaitForSelector))
        {
            await page.WaitForSelectorAsync(request.WaitForSelector, new WaitForSelectorOptions { Timeout = request.WaitTimeout ?? 10000 });
        }

        // Emulate media type screen
        await page.EmulateMediaTypeAsync(MediaType.Screen);

        // Generate PDF
        await page.PdfAsync(outputPdfPath, new PdfOptions
        {
            Format = PaperFormat.A4,
            PrintBackground = true
        });

        Console.WriteLine($"PDF saved to: {outputPdfPath}");

        // 3. Print PDF
        PrintPdf(outputPdfPath);
        Console.WriteLine("Sent to default printer");

        // 4. Cleanup
        try
        {
            if (File.Exists(outputPdfPath))
            {
                File.Delete(outputPdfPath);
                Console.WriteLine($"Deleted PDF: {outputPdfPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error deleting PDF: {ex.Message}");
        }

        return Results.Ok("Printed successfully");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error processing request: {ex.Message}");
        
        // Attempt cleanup on error
        try
        {
            if (File.Exists(outputPdfPath)) File.Delete(outputPdfPath);
        }
        catch { /* ignore */ }

        return Results.Problem($"Error processing print request: {ex.Message}");
    }
});

// Helper URL to verify server is running
app.MapGet("/", () => "NetPrintUrl API is running. POST to /print to print.");

app.Run("http://localhost:3000");

// --- Helper Methods & Classes ---

void PrintPdf(string pdfPath)
{
    try
    {
        using var document = PdfDocument.Load(pdfPath);
        using var printDocument = document.CreatePrintDocument();
        
        // Use default printer
        printDocument.PrinterSettings.PrintToFile = false;
        printDocument.Print();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Print Error: {ex.Message}");
        throw; // Re-throw to be caught by the API handler
    }
}

record PrintRequest(string Url, string? WaitForSelector, int? WaitTimeout);
