using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Backup;

/// <summary>
/// The human-readable archive: used by the nightly backup (everything), the owner's
/// "download Excel" button and the period archive. Headers are Turkish and German,
/// the two languages of the salon, exactly like the Node version's workbook.
/// </summary>
public sealed class ExcelExport(AppDbContext db, AppSettings settings)
{
    private const string Money = "#,##0.00";

    private static readonly Dictionary<SessionStatus, string> StatusNames = new()
    {
        [SessionStatus.Completed] = "Tamamlandı",
        [SessionStatus.Cancelled] = "İptal",
        [SessionStatus.Active] = "Devam ediyor",
    };

    public async Task<byte[]> BuildAsync(ExportRange range)
    {
        var sessions = await range.Apply(db.Sessions.AsNoTracking())
            .Include(s => s.Items).ThenInclude(i => i.Service)
            .Include(s => s.Employee)
            .OrderBy(s => s.StartedAt)
            .AsSplitQuery()
            .ToListAsync();
        var services = await db.Services.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync();
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
        var zone = settings.SalonTimeZone;

        using var workbook = new XLWorkbook();

        var records = Sheet(workbook, "Kayıtlar",
            ("ID", 8), ("Tarih / Datum", 12), ("Saat / Uhrzeit", 9), ("Çalışan / Mitarbeiter", 18),
            ("İşlemler / Leistungen", 50), ("Süre dk / Dauer Min.", 12), ("Toplam € / Gesamt €", 14),
            ("Durum / Status", 14), ("Düzeltildi / Korrigiert", 12));
        var row = 2;
        foreach (var s in sessions)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(s.StartedAt, zone);
            records.Cell(row, 1).Value = s.Id;
            records.Cell(row, 2).Value = SalonTime.Format(DateOnly.FromDateTime(local));
            records.Cell(row, 3).Value = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            records.Cell(row, 4).Value = s.Employee.Name;
            records.Cell(row, 5).Value = string.Join(", ", s.Items.OrderBy(i => i.Id).Select(ItemLabel));
            if (s.FinishedAt is { } finished) records.Cell(row, 6).Value = Math.Round((finished - s.StartedAt).TotalMinutes, MidpointRounding.AwayFromZero);
            records.Cell(row, 7).Value = s.Items.Sum(i => i.LineTotalCents) / 100m;
            records.Cell(row, 8).Value = StatusNames[s.Status];
            records.Cell(row, 9).Value = s.EditedAt is null ? "" : "Evet / Ja";
            row++;
        }
        records.Column(7).Style.NumberFormat.Format = Money;

        var items = Sheet(workbook, "Kalemler",
            ("Kayıt ID", 10), ("Tarih / Datum", 12), ("Çalışan / Mitarbeiter", 18), ("İşlem (TR)", 24),
            ("Leistung (DE)", 24), ("Açıklama / Notiz", 24), ("Birim € / Einzel €", 12), ("Adet / Anzahl", 10),
            ("Tutar € / Betrag €", 12));
        row = 2;
        foreach (var s in sessions)
        {
            foreach (var i in s.Items.OrderBy(i => i.Id))
            {
                items.Cell(row, 1).Value = s.Id;
                items.Cell(row, 2).Value = SalonTime.Format(SalonTime.DayOf(s.StartedAt, zone));
                items.Cell(row, 3).Value = s.Employee.Name;
                items.Cell(row, 4).Value = i.Service.NameTr;
                items.Cell(row, 5).Value = i.Service.NameDe;
                items.Cell(row, 6).Value = i.Note ?? "";
                items.Cell(row, 7).Value = i.PriceCentsSnapshot / 100m;
                items.Cell(row, 8).Value = i.Quantity;
                items.Cell(row, 9).Value = i.LineTotalCents / 100m;
                row++;
            }
        }
        items.Column(7).Style.NumberFormat.Format = Money;
        items.Column(9).Style.NumberFormat.Format = Money;

        var priceList = Sheet(workbook, "Fiyat Listesi",
            ("ID", 8), ("İsim (TR)", 24), ("Name (DE)", 24), ("Fiyat € / Preis €", 12), ("Aktif / Aktiv", 10));
        row = 2;
        foreach (var svc in services)
        {
            priceList.Cell(row, 1).Value = svc.Id;
            priceList.Cell(row, 2).Value = svc.NameTr;
            priceList.Cell(row, 3).Value = svc.NameDe;
            // the custom service has no price of its own; 0 would read as "free"
            if (svc.Custom) priceList.Cell(row, 4).Value = "Her kayıtta girilir / Pro Eintrag";
            else priceList.Cell(row, 4).Value = svc.PriceCents / 100m;
            priceList.Cell(row, 5).Value = svc.Active ? "Evet / Ja" : "Hayır / Nein";
            row++;
        }
        priceList.Column(4).Style.NumberFormat.Format = Money;

        // no password hashes; deleted accounts stay listed so the archive still explains who is who
        var employees = Sheet(workbook, "Çalışanlar",
            ("ID", 8), ("İsim / Name", 20), ("Kullanıcı adı / Benutzername", 20), ("Rol / Rolle", 12), ("Durum / Status", 18));
        row = 2;
        foreach (var u in users)
        {
            employees.Cell(row, 1).Value = u.Id;
            employees.Cell(row, 2).Value = u.Name;
            employees.Cell(row, 3).Value = u.DisplayUsername;
            employees.Cell(row, 4).Value = u.Role == Role.Admin ? "Yönetici / Chef" : "Çalışan / Mitarbeiter";
            employees.Cell(row, 5).Value = u.DeletedAt is not null ? "Silindi / Gelöscht" : u.Active ? "Aktif / Aktiv" : "Pasif / Inaktiv";
            row++;
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private static string ItemLabel(SessionItem i) =>
        i.Service.NameTr + (i.Note is null ? "" : $" ({i.Note})") + (i.Quantity > 1 ? $" ×{i.Quantity}" : "");

    private static IXLWorksheet Sheet(XLWorkbook workbook, string name, params (string Header, double Width)[] columns)
    {
        var sheet = workbook.Worksheets.Add(name);
        for (var c = 0; c < columns.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = columns[c].Header;
            sheet.Column(c + 1).Width = columns[c].Width;
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        return sheet;
    }
}
