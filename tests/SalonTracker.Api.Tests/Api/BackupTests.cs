using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalonTracker.Api.Backup;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;
using SalonTracker.Api.Tests.Infrastructure;

namespace SalonTracker.Api.Tests.Api;

public sealed class BackupTests(ApiFixture fixture) : ApiTest(fixture)
{
    private async Task<byte[]> ExportJson()
    {
        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JsonBackup>().ExportAsync(ExportRange.All);
    }

    private Task<HttpResponseMessage> Restore(string cookie, byte[] file)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/system/restore") { Content = new ByteArrayContent(file) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        request.Headers.Add("Cookie", cookie);
        return Client.SendAsync(request);
    }

    [Fact]
    public void The_wrong_file_gets_a_message_that_says_why()
    {
        var zip = Encoding.ASCII.GetBytes("PK\u0003\u0004 the rest of a zip");
        Assert.Equal("zip_selected", Assert.Throws<ApiException>(() => JsonBackup.Parse(zip)).Code);
        Assert.Equal("invalid_backup_file", Assert.Throws<ApiException>(() => JsonBackup.Parse("not gzip"u8.ToArray())).Code);

        using var other = new MemoryStream();
        using (var gzip = new GZipStream(other, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write("""{"format":2,"sessions":[]}"""u8);
        }
        Assert.Equal("invalid_backup_file", Assert.Throws<ApiException>(() => JsonBackup.Parse(other.ToArray())).Code);
    }

    [Fact]
    public async Task A_restore_adds_only_what_is_missing_and_running_it_twice_changes_nothing()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService();
        var kept = (await RecordSession(ali.Cookie, Item(haircut.Id))).GetProperty("id").GetInt32();
        var lost = (await RecordSession(ali.Cookie, Item(haircut.Id, quantity: 2))).GetProperty("id").GetInt32();
        var backup = await ExportJson();

        // after the backup: one record is lost, another one is corrected
        await WithDb(async db =>
        {
            await db.Sessions.Where(s => s.Id == lost).ExecuteDeleteAsync();
            await db.SessionItems.Where(i => i.SessionId == kept).ExecuteUpdateAsync(u => u.SetProperty(i => i.Quantity, 5));
        });

        var first = await Json(await Restore(owner.Cookie, backup));
        Assert.Equal(1, first.GetProperty("added").GetProperty("sessions").GetInt32());
        Assert.Equal(1, first.GetProperty("added").GetProperty("items").GetInt32());
        Assert.Equal(0, first.GetProperty("added").GetProperty("users").GetInt32());
        // existing rows are never overwritten by the older copy
        Assert.Equal(5, await WithDb(db => db.SessionItems.Where(i => i.SessionId == kept).Select(i => i.Quantity).SingleAsync()));

        var second = await Json(await Restore(owner.Cookie, backup));
        Assert.Equal(0, second.GetProperty("added").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Ids_continue_after_the_restored_rows()
    {
        var owner = await CreateUser(role: Role.Admin);
        var haircut = await CreateService();
        await RecordSession(owner.Cookie, Item(haircut.Id));
        var backup = await ExportJson();

        await Fixture.ResetAsync();
        await WithDb(async db =>
        {
            var summary = await new JsonBackup(db, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonBackup>.Instance)
                .ImportAsync(JsonBackup.Parse(backup));
            Assert.Equal(4, summary.Added.Total);
        });

        // without moving the identity sequences this would collide with the restored id 1
        var next = await CreateService("Beard");
        Assert.True(next.Id > haircut.Id);
    }

    [Fact]
    public async Task A_backup_written_by_the_Node_version_restores_and_its_users_can_sign_in()
    {
        var owner = await CreateUser(role: Role.Admin, username: "owner");
        var file = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "node-backup.json.gz"));

        var response = await Restore(owner.Cookie, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await Json(response);
        // the owner created above holds id 1, so the Node file's own id 1 is skipped
        Assert.Equal(3, summary.GetProperty("found").GetProperty("users").GetInt32());
        Assert.Equal(2, summary.GetProperty("added").GetProperty("users").GetInt32());
        Assert.Equal(4, summary.GetProperty("added").GetProperty("sessions").GetInt32());

        // bcryptjs hashes from Node verify here
        var login = await Post("/api/auth/login", body: new { username = "hasan", password = "tarak-1234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("de", (await Json(login)).GetProperty("user").GetProperty("locale").GetString());

        // snapshots, the custom note and the deleted account came through unchanged
        var stats = await Json(await Get("/api/stats/overview?from=2026-07-14&to=2026-07-16", owner.Cookie));
        Assert.Equal(1800 + 3550 + 2 * 1200 + 2000, stats.GetProperty("revenueCents").GetInt32());
        var users = await Json(await Get("/api/users", owner.Cookie));
        var deniz = users.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("name").GetString() == "Deniz");
        Assert.True(deniz.GetProperty("deleted").GetBoolean());
        Assert.Equal("deniz", deniz.GetProperty("username").GetString());
        Assert.Equal("bridal updo", await WithDb(db => db.SessionItems.Where(i => i.Note != null).Select(i => i.Note).SingleAsync()));
    }

    [Fact]
    public async Task Cleanup_refuses_to_delete_without_a_fresh_backup_and_works_after_one()
    {
        // "fresh" compares the clock with the backup files' timestamps, which are real
        Fixture.Clock.UseRealTime();
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService();
        await RecordSession(ali.Cookie, Item(haircut.Id));
        var tomorrow = SalonTimeTomorrow();

        var refused = await Post("/api/system/cleanup", owner.Cookie, new { before = tomorrow, confirm = "DELETE" });
        Assert.Equal("backup_required", await ErrorCode(refused));

        var backup = await Json(await Post("/api/system/backup", owner.Cookie));
        var result = backup.GetProperty("result");
        Assert.True(result.GetProperty("excel").GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("json").GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("dump").GetProperty("ok").GetBoolean()); // no pg_dump in tests

        var wrongWord = await Post("/api/system/cleanup", owner.Cookie, new { before = tomorrow, confirm = "SIL" });
        Assert.Equal("validation", await ErrorCode(wrongWord));

        var done = await Json(await Post("/api/system/cleanup", owner.Cookie, new { before = tomorrow, confirm = "DELETE" }));
        Assert.Equal(1, done.GetProperty("deletedSessions").GetInt32());
    }

    [Fact]
    public async Task The_latest_backup_downloads_as_one_zip()
    {
        var owner = await CreateUser(role: Role.Admin);
        Assert.Equal("no_backup", await ErrorCode(await Get("/api/system/backup/latest.zip", owner.Cookie)));
        await Post("/api/system/backup", owner.Cookie);

        var response = await Get("/api/system/backup/latest.zip", owner.Cookie);

        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal([".json.gz", ".xlsx"], zip.Entries.Select(e => e.Name[e.Name.IndexOf('.')..]).Order());
    }

    [Theory]
    [InlineData("..%2F..%2Fappsettings.json")]
    [InlineData("last-backup.json")]
    [InlineData("friseur-2026-01-01.dump.exe")]
    public async Task Only_backup_files_can_be_downloaded(string name)
    {
        var owner = await CreateUser(role: Role.Admin);

        var response = await Get($"/api/system/backup/file/{name}", owner.Cookie);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_Excel_export_has_four_sheets_with_snapshot_prices()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser(name: "Ali");
        var haircut = await CreateService(priceCents: 2000);
        await RecordSession(ali.Cookie, Item(haircut.Id, quantity: 2));

        var response = await Get($"/api/export/sessions.xlsx?from={Today}&to={Today}", owner.Cookie);

        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        Assert.Equal(["Kayıtlar", "Kalemler", "Fiyat Listesi", "Çalışanlar"], workbook.Worksheets.Select(w => w.Name));
        var records = workbook.Worksheet("Kayıtlar");
        Assert.Equal("Ali", records.Cell(2, 4).GetString());
        Assert.Equal(40m, records.Cell(2, 7).GetValue<decimal>());
    }

    private string SalonTimeTomorrow() => DateOnly.Parse(Today).AddDays(1).ToString("yyyy-MM-dd");
}
