using Microsoft.Extensions.Options;
using Npgsql;

namespace ErrorLensLLM.Demo;

public sealed record Ogrenci(int Id, string Durum, string Donem);
public sealed record NotBilgisi(string DersKodu, int Puan);

/// <summary>Endpoints of the three modules. Unhandled exceptions are logged by ASP.NET Core and reach the collector.</summary>
public static class Modules
{
    public static void MapKayit(WebApplication app)
    {
        app.MapGet("/ogrenciler/{id:int}", async (int id, NpgsqlDataSource db, IOptions<DemoOptions> options, ILogger<KayitService> log) =>
        {
            var ogrenci = await new KayitService(db, options.Value).GetirAsync(id);
            if (ogrenci is null)
            {
                log.LogWarning("Ogrenci {OgrenciId} bulunamadi", id);
                return Results.NotFound();
            }

            return Results.Ok(ogrenci);
        });
    }

    public static void MapNot(WebApplication app)
    {
        app.MapGet("/notlar/{ogrenciId:int}", async (int ogrenciId, IHttpClientFactory http, NpgsqlDataSource db, IOptions<DemoOptions> options, ILogger<NotService> log) =>
        {
            var ogrenci = await KayitClient.GetirAsync(http, ogrenciId);
            if (ogrenci is null)
                return Results.NotFound();

            var notlar = await new NotService(db, options.Value).ListeleAsync(ogrenciId);
            log.LogInformation("Ogrenci {OgrenciId} icin {NotSayisi} not listelendi", ogrenciId, notlar.Count);
            return Results.Ok(notlar);
        });
    }

    public static void MapOdeme(WebApplication app)
    {
        app.MapPost("/odemeler/{ogrenciId:int}", async (int ogrenciId, IHttpClientFactory http, IOptions<DemoOptions> options, ILogger<OdemeService> log) =>
        {
            var ogrenci = await KayitClient.GetirAsync(http, ogrenciId);
            if (ogrenci is null)
                return Results.NotFound();
            if (ogrenci.Durum != "aktif")
            {
                log.LogWarning("Pasif ogrenci {OgrenciId} icin odeme reddedildi", ogrenciId);
                return Results.Conflict();
            }

            var referans = await new OdemeService(options.Value).TahsilEtAsync(ogrenciId);
            log.LogInformation("Ogrenci {OgrenciId} odemesi alindi, banka referansi {Referans}", ogrenciId, referans);
            return Results.Ok(new { referans });
        });
    }
}

public sealed class KayitService(NpgsqlDataSource db, DemoOptions options)
{
    public async Task<Ogrenci?> GetirAsync(int id)
    {
        // Version 1.1 reads the new term column, but its migration was never run on this database
        var sql = options.Has(Faults.KayitMissingColumn)
            ? "select id, durum, donem_id::text from ogrenci where id = $1"
            : "select id, durum, donem from ogrenci where id = $1";

        await using var command = db.CreateCommand(sql);
        command.Parameters.Add(new() { Value = id });
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new Ogrenci(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)) : null;
    }
}

public sealed class NotService(NpgsqlDataSource db, DemoOptions options)
{
    public async Task<List<NotBilgisi>> ListeleAsync(int ogrenciId)
    {
        await using var command = db.CreateCommand("select ders_kodu, puan from ogrenci_not where ogrenci_id = $1 order by ders_kodu");
        command.Parameters.Add(new() { Value = ogrenciId });
        await using var reader = await command.ExecuteReaderAsync();

        var notlar = new List<NotBilgisi>();
        while (await reader.ReadAsync())
            notlar.Add(new NotBilgisi(reader.GetString(0), reader.GetInt32(1)));

        if (options.Has(Faults.NotNullReference))
        {
            // Version 1.1 shows the elective course first and assumes every student has one
            var secmeli = notlar.FirstOrDefault(n => n.DersKodu.StartsWith("ING"));
            notlar.Insert(0, secmeli! with { DersKodu = secmeli.DersKodu + " (secmeli)" });
        }

        return notlar;
    }
}

public sealed class OdemeService(DemoOptions options)
{
    public async Task<string> TahsilEtAsync(int ogrenciId)
    {
        // Stand-in for the bank: normally 20–80 ms
        await Task.Delay(Random.Shared.Next(20, 80));
        if (options.Has(Faults.OdemeBankTimeout) && Random.Shared.NextDouble() < 0.4)
            throw new TimeoutException($"Banka {ogrenciId} numarali odeme icin 30 sn icinde yanit vermedi");

        return $"BNK{Random.Shared.Next(100000, 999999)}";
    }
}

/// <summary>Calls the kayit module; the traceparent header is added by HttpClient, so the trace spans modules.</summary>
public static class KayitClient
{
    public static async Task<Ogrenci?> GetirAsync(IHttpClientFactory http, int ogrenciId)
    {
        using var response = await http.CreateClient("kayit").GetAsync($"ogrenciler/{ogrenciId}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Ogrenci>();
    }
}
