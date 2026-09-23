using Microsoft.EntityFrameworkCore;
using HtmlAgilityPack;
using Npgsql;

// CAMBIO 1: evita errores de fechas/zona horaria al guardar en PostgreSQL
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// CAMBIO 2: en la nube el puerto lo da la plataforma (variable PORT).
// En tu compu sigue usando 5016.
var puertoServidor = Environment.GetEnvironmentVariable("PORT") ?? "5016";
builder.WebHost.UseUrls($"http://0.0.0.0:{puertoServidor}");

// CAMBIO 3: la nube entrega DATABASE_URL como "postgres://usuario:clave@host/base".
// Esta funcion la convierte al formato que entiende Npgsql.
var conexionNube = ConstruirConexion(Environment.GetEnvironmentVariable("DATABASE_URL"));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(conexionNube));

// Motor automatico (cada 2 horas)
builder.Services.AddHostedService<MotorAutomatico>();

var app = builder.Build();

// Crea la base de datos y las tablas automaticamente si no existen
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// --- ENDPOINTS DE LA APLICACION ANDROID ---

// 1. Pide los sorteos del dia de hoy
app.MapGet("/api/sorteos/hoy", async (AppDbContext db) =>
{
    var hoy = HoyArgentina();
    var sorteos = await db.Sorteos
        .Include(s => s.Posiciones)
        .Where(s => s.Fecha.Date == hoy)
        .ToListAsync();

    return Results.Ok(sorteos);
});

// 2. Pide el historial de las ultimas 5 fechas
app.MapGet("/api/sorteos/fechas", async (AppDbContext db) =>
{
    var fechas = await db.Sorteos
        .Select(s => s.Fecha.Date)
        .Distinct()
        .OrderByDescending(f => f)
        .Take(5)
        .Select(f => f.ToString("yyyy-MM-dd"))
        .ToListAsync();

    return Results.Ok(fechas);
});

// 3. Pide los sorteos de un dia exacto del historial
app.MapGet("/api/sorteos/fecha/{fechaBuscada}", async (string fechaBuscada, AppDbContext db) =>
{
    if (DateTime.TryParse(fechaBuscada, out DateTime fechaReal))
    {
        var sorteos = await db.Sorteos
            .Include(s => s.Posiciones)
            .Where(s => s.Fecha.Date == fechaReal.Date)
            .ToListAsync();

        return Results.Ok(sorteos);
    }
    return Results.BadRequest("Fecha incorrecta");
});


// --- EL SCRAPER ROBOT ---
app.MapGet("/api/scraper/real", async (AppDbContext db) =>
{
    try
    {
        var hoy = HoyArgentina();
        var url = "https://www.laquinieladetucuman.com.ar/";
        var web = new HtmlWeb();
        // CAMBIO 6: se presenta como un navegador comun; algunas paginas rechazan pedidos de "robots"
        web.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
        var documento = web.Load(url);

        // Buscamos todas las cajas de sorteos
        var titulos = documento.DocumentNode.SelectNodes("//h4[contains(translate(text(), 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'), 'QUINIELA')]");

        if (titulos == null)
        {
            // CAMBIO 7: si falla, muestra que recibio el servidor para poder diagnosticar
            var tituloPagina = documento.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim() ?? "(sin titulo)";
            var textoPagina = System.Text.RegularExpressions.Regex.Replace(documento.DocumentNode.InnerText, @"\s+", " ").Trim();
            if (textoPagina.Length > 300) textoPagina = textoPagina.Substring(0, 300);
            return Results.BadRequest($"No se encontraron sorteos. Titulo: {tituloPagina} | Largo: {documento.DocumentNode.InnerHtml.Length} | Inicio: {textoPagina}");
        }

        int sorteosGuardados = 0;

        foreach (var titulo in titulos)
        {
            string nombreSorteo = titulo.InnerText.Trim().ToUpper().Replace("QUINIELA", "").Trim();

            var cajaCompleta = titulo.Ancestors("div").FirstOrDefault(d =>
                d.InnerHtml.Contains("result-numero") || d.InnerHtml.Contains("Aún no hay resultados"));

            if (cajaCompleta != null)
            {
                if (cajaCompleta.InnerHtml.Contains("Aún no hay resultados"))
                {
                    continue;
                }

                var nodosNumeros = cajaCompleta.SelectNodes(".//td[contains(@class, 'result-numero')]/span");

                if (nodosNumeros != null && nodosNumeros.Count > 0)
                {
                    // Aniquilador de duplicados
                    var sorteosViejos = await db.Sorteos.Where(s => s.Fecha.Date == hoy && s.TipoSorteo == nombreSorteo).ToListAsync();
                    if (sorteosViejos.Any())
                    {
                        db.Sorteos.RemoveRange(sorteosViejos);
                        await db.SaveChangesAsync();
                    }

                    var posicionesList = new List<PosicionSorteo>();
                    int cantidad = Math.Min(20, nodosNumeros.Count);
                    for (int i = 0; i < cantidad; i++)
                    {
                        posicionesList.Add(new PosicionSorteo { Posicion = i + 1, Numero = nodosNumeros[i].InnerText.Trim() });
                    }

                    db.Sorteos.Add(new Sorteo { Fecha = hoy, TipoSorteo = nombreSorteo, Posiciones = posicionesList });
                    await db.SaveChangesAsync();
                    sorteosGuardados++;
                }
            }
        }

        return Results.Ok($"¡Éxito! Se analizaron y guardaron {sorteosGuardados} sorteos de hoy.");
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Fallo técnico en el Scraper: {ex.Message}");
    }
});

app.Run();

// --- FUNCIONES AUXILIARES ---

// CAMBIO 4: el servidor en la nube usa hora UTC (3 horas adelantada respecto a Tucuman).
// Sin esto, el sorteo de las 21:00 se guardaria con la fecha de "mañana".
static DateTime HoyArgentina() => DateTime.UtcNow.AddHours(-3).Date;

static string ConstruirConexion(string? url)
{
    // En tu compu (sin DATABASE_URL) usa la base local de prueba
    if (string.IsNullOrWhiteSpace(url))
        return "Host=localhost;Database=quinieladb;Username=postgres;Password=admin";

    // Si ya viene en formato Npgsql ("Host=...;"), se usa tal cual
    if (!url.StartsWith("postgres://") && !url.StartsWith("postgresql://"))
        return url;

    var uri = new Uri(url);
    var datos = uri.UserInfo.Split(':', 2);

    var b = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(datos[0]),
        Password = datos.Length > 1 ? Uri.UnescapeDataString(datos[1]) : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Prefer,
        TrustServerCertificate = true
    };
    return b.ConnectionString;
}

// --- MODELOS DE BASE DE DATOS ---
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Sorteo> Sorteos { get; set; }
    public DbSet<PosicionSorteo> Posiciones { get; set; }
}

public class Sorteo
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; }
    public string TipoSorteo { get; set; } = string.Empty;
    public List<PosicionSorteo> Posiciones { get; set; } = new();
}

public class PosicionSorteo
{
    public int Id { get; set; }
    public int SorteoId { get; set; }
    public int Posicion { get; set; }
    public string Numero { get; set; } = string.Empty;
}

// --- MOTOR AUTOMATICO ---
public class MotorAutomatico : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // CAMBIO 5: espera a que el servidor termine de arrancar antes de la primera consulta
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var cliente = new HttpClient();
                var puerto = Environment.GetEnvironmentVariable("PORT") ?? "5016";
                await cliente.GetAsync($"http://localhost:{puerto}/api/scraper/real", stoppingToken);
            }
            catch { /* Ignorar errores de red temporales */ }

            await Task.Delay(TimeSpan.FromHours(2), stoppingToken);
        }
    }
}