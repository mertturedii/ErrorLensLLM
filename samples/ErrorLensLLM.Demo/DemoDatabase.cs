using Npgsql;

namespace ErrorLensLLM.Demo;

/// <summary>Fake student data (ids and course codes only, no personal data) in its own database.</summary>
public static class DemoDatabase
{
    public const int StudentCount = 500;
    private static readonly string[] Courses = ["BLM101", "BLM203", "MAT101", "FIZ102", "TUR101", "ING201"];

    public static async Task EnsureAsync(string connectionString)
    {
        // The demo database is created on first run, next to the collector's "errorlens" database
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database!;
        await using (var admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString))
        {
            await using var exists = admin.CreateCommand("select 1 from pg_database where datname = $1");
            exists.Parameters.Add(new() { Value = database });
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = admin.CreateCommand($"create database \"{database}\"");
                await create.ExecuteNonQueryAsync();
            }
        }

        await using var db = NpgsqlDataSource.Create(connectionString);
        await using var schema = db.CreateCommand($"""
            create table if not exists ogrenci (id int primary key, durum text not null, donem text not null);
            create table if not exists ogrenci_not (ogrenci_id int not null references ogrenci (id), ders_kodu text not null, puan int not null);
            insert into ogrenci (id, durum, donem)
                select i, case when i % 17 = 0 then 'pasif' else 'aktif' end, '2026-Guz'
                from generate_series(1, {StudentCount}) i
                on conflict do nothing;
            insert into ogrenci_not (ogrenci_id, ders_kodu, puan)
                select o.id, c.kod, (o.id * 7 + length(c.kod) * 13) % 101
                from ogrenci o cross join unnest(array['{string.Join("','", Courses)}']) c(kod)
                where not exists (select 1 from ogrenci_not)
                  and (o.id + length(c.kod)) % 3 <> 0;
            """);
        await schema.ExecuteNonQueryAsync();
    }
}
