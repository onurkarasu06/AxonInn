using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class AnaController : Controller
    {
        private readonly AxonInnContext _context;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public AnaController(AxonInnContext context)
        {
            _context = context;
        }
        private Personel? GetActiveUser()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                return JsonSerializer.Deserialize<Personel>(personelJson);
            }
            catch
            {
                return null;
            }
        }

        [Route("AnaSayfa")]
        public async Task<IActionResult> Ana()
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();

                if (loginOlanPersonel == null)
                    return RedirectToAction("Login", "Login");

                // ⚡ OPTİMİZASYON: Tüm null check'leri Select içinde halledip doğrudan temiz veri alıyoruz.
                var sessionBilgisi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new {
                        HotelId = d.HotelRef, 
                        HotelAdi = d.HotelRefNavigation.Adi ?? "Bilinmeyen Otel",
                        DepartmanAdi = d.Adi ?? "Bilinmeyen Departman"
                    })
                    .FirstOrDefaultAsync();

                if (sessionBilgisi == null || sessionBilgisi.HotelId == 0)
                    return RedirectToAction("Login", "Login");

                long hotelId = sessionBilgisi.HotelId;

                // --- 🚀 BASE QUERY TEMİZLİĞİ ---
                // Navigation property'ler için != null kontrolü kaldırıldı (INNER JOIN'e zorlayıp SQL'i hızlandırır).
                IQueryable<Personel> personelQuery = _context.Personels.AsNoTracking().Where(p => p.AktifMi == 1);
                IQueryable<Gorev> gorevQuery = _context.Gorevs.AsNoTracking().Where(g => g.PersonelRefNavigation.AktifMi == 1);
                IQueryable<Departman> departmanQuery = _context.Departmen.AsNoTracking();

                // Yetki Filtrelemeleri (Daha sade)
                if (loginOlanPersonel.Yetki == 3)
                {
                    personelQuery = personelQuery.Where(p => p.Id == loginOlanPersonel.Id);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRef == loginOlanPersonel.Id);
                    departmanQuery = departmanQuery.Where(d => d.Id == loginOlanPersonel.DepartmanRef);
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    personelQuery = personelQuery.Where(p => p.DepartmanRef == loginOlanPersonel.DepartmanRef);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRefNavigation.DepartmanRef == loginOlanPersonel.DepartmanRef);
                    departmanQuery = departmanQuery.Where(d => d.Id == loginOlanPersonel.DepartmanRef);
                }
                else
                {
                    personelQuery = personelQuery.Where(p => p.DepartmanRefNavigation.HotelRef == hotelId);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId);
                    departmanQuery = departmanQuery.Where(d => d.HotelRef == hotelId);
                }

                // ⚡ SQL SORGULARI 
                var departmanPersonelSayilariDb = await personelQuery
                    .GroupBy(p => p.DepartmanRefNavigation.Adi)
                    .Select(g => new { departmanAd = g.Key, adet = g.Count() })
                    .ToListAsync();

                var gorevChartDataDb = await gorevQuery
                    .GroupBy(g => new {
                        pId = g.PersonelRef,
                        ad = g.PersonelRefNavigation.Adi,
                        soyad = g.PersonelRefNavigation.Soyadi,
                        dept = g.PersonelRefNavigation.DepartmanRefNavigation.Adi
                    })
                    .Select(g => new {
                        g.Key.pId,
                        g.Key.ad,
                        g.Key.soyad,
                        g.Key.dept,
                        beklemede = g.Count(x => x.Durum == 1),
                        islemde = g.Count(x => x.Durum == 2),
                        tamamlandi = g.Count(x => x.Durum == 3)
                    }).ToListAsync();

                // ⚡ OPTİMİZASYON: !string.IsNullOrEmpty SQL'de daha performanslıdır
                var aiKategoriDb = await gorevQuery
                    .Where(g => !string.IsNullOrEmpty(g.AiKategori))
                    .GroupBy(g => g.AiKategori)
                    .Select(g => new { kategori = g.Key, adet = g.Count() })
                    .ToListAsync();

                var departmanlar = await departmanQuery
                    .Select(d => new Departman { Id = d.Id, Adi = d.Adi })
                    .ToListAsync();

                // --- 🚀 RAM (C#) İŞLEMLERİ VE FORMATLAMA ---
                var departmanPersonelSayilari = departmanPersonelSayilariDb.Select(x => new {
                    departmanAd = x.departmanAd ?? "Belirtilmemiş",
                    adet = x.adet
                }).ToList();

                var gorevChartData = gorevChartDataDb.Select(g => new {
                    g.pId,
                    ad = string.IsNullOrWhiteSpace(g.soyad) ? (g.ad ?? "").Trim() : $"{g.ad} {g.soyad}".Trim(),
                    dept = g.dept ?? "Belirtilmemiş",
                    g.beklemede,
                    g.islemde,
                    g.tamamlandi
                }).ToList();

                int toplamKategorizeGorev = aiKategoriDb.Sum(x => x.adet);
                var aiChartData = aiKategoriDb.Select(x => new {
                    x.kategori,
                    x.adet,
                    yuzde = toplamKategorizeGorev > 0 ? Math.Round(((double)x.adet / toplamKategorizeGorev) * 100, 1) : 0
                }).OrderByDescending(x => x.yuzde).ToList();

                // --- 🚀 VIEW BAG ATAMALARI ---
                ViewBag.AiKategoriJson = JsonSerializer.Serialize(aiChartData, _jsonOptions);
                ViewBag.PersonelJson = JsonSerializer.Serialize(departmanPersonelSayilari, _jsonOptions);
                ViewBag.GorevJson = JsonSerializer.Serialize(gorevChartData, _jsonOptions);

                ViewBag.HotelAdi = sessionBilgisi.HotelAdi;
                ViewBag.AktifPersonelAdet = departmanPersonelSayilari.Sum(x => x.adet);
                ViewBag.BeklemedeAdet = gorevChartData.Sum(x => x.beklemede);
                ViewBag.IslemdeAdet = gorevChartData.Sum(x => x.islemde);
                ViewBag.BittiAdet = gorevChartData.Sum(x => x.tamamlandi);
                ViewBag.Departmanlar = departmanlar;

                await LogKaydetAsync(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", "Dashboard Görüntüleme", sessionBilgisi.HotelAdi, sessionBilgisi.DepartmanAdi);

                return View();
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        private async Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string yeniDeger, string oncedenAlinanHotelAd = "", string oncedenAlinanDepartmanAd = "")
        {
            try
            {
                if (personel == null) return false;

                string departmanAdi = oncedenAlinanDepartmanAd;
                string hotelAdi = oncedenAlinanHotelAd;

                if (personel.DepartmanRef != 0 && string.IsNullOrWhiteSpace(hotelAdi))
                {
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : string.Empty })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
                        departmanAdi = depBilgisi.Adi ?? string.Empty;
                        hotelAdi = depBilgisi.HotelAdi ?? string.Empty;
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "SayfaZiyareti",
                    KayitRefId = personel.Id,
                    IslemTipi = islemTipi,
                    EskiDeger = string.Empty,
                    YeniDeger = yeniDeger ?? string.Empty,
                    YapanHotelAd = hotelAdi,
                    YapanDepartmanAd = departmanAdi,
                    YapanAdSoyad = string.IsNullOrWhiteSpace(personel.Soyadi) ? (personel.Adi ?? string.Empty).Trim() : string.Concat(personel.Adi, " ", personel.Soyadi).Trim()
                };

                _context.AuditLogs.Add(log);
                await _context.SaveChangesAsync();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}