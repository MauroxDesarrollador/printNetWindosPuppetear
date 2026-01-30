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

// DESCARGAR NAVEGADOR UNA SOLA VEZ AL INICIO
Console.WriteLine("Downloading browser (if needed)...");
var browserFetcher = new BrowserFetcher();
await browserFetcher.DownloadAsync();
Console.WriteLine("Browser ready.");

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
        // Launch Browser (el navegador ya fue descargado al inicio)
        var launchOptions = new LaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
        };

        using var browser = await Puppeteer.LaunchAsync(launchOptions);
        using var page = await browser.NewPageAsync();
        
        // BLOQUEAR PETICIONES AL ENDPOINT /print PARA EVITAR LOOPS
        await page.SetRequestInterceptionAsync(true);
        page.Request += async (sender, e) =>
        {
            // Bloquear peticiones al endpoint /print para evitar loop infinito
            if (e.Request.Url.Contains("localhost:3000/print"))
            {
                Console.WriteLine($"Blocked recursive print request from Puppeteer");
                await e.Request.AbortAsync();
            }
            else
            {
                await e.Request.ContinueAsync();
            }
        };
        
        // Viewport - Set to match print width approximately
        await page.SetViewportAsync(new ViewPortOptions { Width = 380, Height = 800 });

        // Agregar parámetro a la URL para que el JSP sepa que está siendo cargado por Puppeteer
        string urlWithParam = request.Url.Contains("?") 
            ? $"{request.Url}&fromPuppeteer=true" 
            : $"{request.Url}?fromPuppeteer=true";

        // Navigate
        var navOptions = new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }, Timeout = 30000 };
        await page.GoToAsync(urlWithParam, navOptions);

        await page.WaitForTimeoutAsync(5000);

        // Inject Styles for Font and Layout
        await page.AddStyleTagAsync(new AddTagOptions
        {
            Content = @"
                @page { margin: 0; size: auto; }
                body { 
                    font-family: 'Courier New', Courier, monospace !important; 
                    font-size: 11pt !important;
                    margin: 0 !important;
                    width: 100% !important;
                }
            "
        });

        // Wait for selector if provided
        if (!string.IsNullOrEmpty(request.WaitForSelector))
        {
            await page.WaitForSelectorAsync(request.WaitForSelector, new WaitForSelectorOptions { Timeout = request.WaitTimeout ?? 10000 });
        }

        // Emulate media type screen
        await page.EmulateMediaTypeAsync(MediaType.Screen);

        // Calculate functionality for Height: Auto
        var height = await page.EvaluateExpressionAsync<object>("document.body.scrollHeight");
        var heightStr = height != null ? height.ToString() + "px" : "297mm";

        Console.WriteLine($"Calculated content height: {heightStr}");

        // Generate PDF
        await page.PdfAsync(outputPdfPath, new PdfOptions
        {
            Width = "72mm",
            Height = heightStr,
            PrintBackground = true,
            MarginOptions = new MarginOptions
            {
                Top = "0mm",
                Bottom = "2mm",
                Left = "2mm",
                Right = "2mm"
            },
            Scale = 1m
        });

        Console.WriteLine($"PDF saved to: {outputPdfPath}");

        // Print PDF
        PrintPdf(outputPdfPath);
        Console.WriteLine("Sent to default printer");

        // CLEANUP - BORRAR PDF DESPUÉS DE IMPRIMIR
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

        return Results.Ok("Printed successfully.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error processing request: {ex.Message}");
        
        // Cleanup on error - intentar borrar el PDF si existe
        try 
        { 
            if (File.Exists(outputPdfPath)) 
            {
                File.Delete(outputPdfPath);
                Console.WriteLine($"Deleted PDF after error: {outputPdfPath}");
            }
        } 
        catch { }
        
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
        
        printDocument.PrintController = new StandardPrintController();
        printDocument.PrinterSettings.PrintToFile = false;

        Console.WriteLine($"Printer: {printDocument.PrinterSettings.PrinterName}");
        
        PaperSize? bestSize = null;
        foreach (PaperSize size in printDocument.PrinterSettings.PaperSizes)
        {
            if (size.Width >= 270 && size.Width <= 330) 
            {
                bestSize = size;
            }
        }

        if (bestSize != null)
        {
            Console.WriteLine($"Setting Paper Size to: {bestSize.PaperName}");
            printDocument.DefaultPageSettings.PaperSize = bestSize;
            printDocument.PrinterSettings.DefaultPageSettings.PaperSize = bestSize;
        }
        else 
        {
            Console.WriteLine("No specific thermal paper size found. Using default.");
        }

        // CONFIGURAR MÁRGENES A CERO Y AJUSTAR POSICIÓN
        printDocument.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        // Aseguramos también los márgenes en PrinterSettings (algunos drivers usan esa configuración)
        try
        {
            printDocument.PrinterSettings.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        }
        catch { }

        // Evitar que la impresora aplique de forma automática su margen físico
        printDocument.OriginAtMargins = false;

        // Añadir un pequeño desplazamiento hacia arriba para compensar el margen físico
        // Ajusta `offsetY` si hace falta (valores negativos suben el contenido)
        int offsetY = -20; // prueba inicial: -20 píxeles (~0.2cm), reducir en valor absoluto si aún hay margen
        printDocument.PrintPage += (s, e) =>
        {
            try { e.Graphics.TranslateTransform(0, offsetY); } catch { }
        };

        // Intentar desactivar duplex si aplica
        if (printDocument.PrinterSettings.CanDuplex)
        {
            printDocument.PrinterSettings.Duplex = Duplex.Simplex;
        }

        printDocument.Print();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Print Error: {ex.Message}");
        throw; 
    }
}

record PrintRequest(string Url, string? WaitForSelector, int? WaitTimeout);