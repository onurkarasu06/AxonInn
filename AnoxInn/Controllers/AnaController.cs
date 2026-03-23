using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

namespace AxonInn.Controllers
{
    public class AnaController : Controller
    {
        private readonly AxonInnContext _context;

        // ⚡ PERFORMANS: JsonSerializerOptions'ı static readonly yaparak her HTTP isteğinde RAM'de yeniden üretilmesini engelliyoruz.
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public AnaController(AxonInnContext context)
        {
            _context = context;
        }

        // ⚡ KOD TEKRARINI ÖNLEME (DRY): Session okuma işlemleri tek merkeze bağlandı, Hata koruması sağlandı.
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

                // ⚡ PERFORMANS: Otel Id ve Adı bilgileri ile birlikte Departman adını TEK bağlantı ile çekiyoruz.
                var sessionBilgisi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new {
                        d.HotelRef,
                        HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : "Bilinmeyen Otel",
                        DepartmanAdi = d.Adi
                    })
                    .FirstOrDefaultAsync();

                if (sessionBilgisi == null || sessionBilgisi.HotelRef == null || sessionBilgisi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                int hotelId = (int)sessionBilgisi.HotelRef;
                string hotelAdi = sessionBilgisi.HotelAdi;
                string departmanAdi = sessionBilgisi.DepartmanAdi ?? "Bilinmeyen Departman";

                // --- 🚀 SIFIR GEREKSİZ JOIN (Sorgu Optimizasyonu) ---
                IQueryable<Personel> personelQuery = _context.Personels.AsNoTracking().Where(p => p.AktifMi == 1);
                IQueryable<Gorev> gorevQuery = _context.Gorevs.AsNoTracking().Where(g => g.PersonelRefNavigation != null && g.PersonelRefNavigation.AktifMi == 1);
                IQueryable<Departman> departmanQuery = _context.Departmen.AsNoTracking();

                if (loginOlanPersonel.Yetki == 3)
                {
                    // Sadece kendi verileri
                    personelQuery = personelQuery.Where(p => p.Id == loginOlanPersonel.Id);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRef == loginOlanPersonel.Id);
                    departmanQuery = departmanQuery.Where(d => d.Id == loginOlanPersonel.DepartmanRef);
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    // Sadece kendi departmanının verileri
                    personelQuery = personelQuery.Where(p => p.DepartmanRef == loginOlanPersonel.DepartmanRef);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRefNavigation != null && g.PersonelRefNavigation.DepartmanRef == loginOlanPersonel.DepartmanRef);
                    departmanQuery = departmanQuery.Where(d => d.Id == loginOlanPersonel.DepartmanRef);
                }
                else
                {
                    // Tüm Otel
                    personelQuery = personelQuery.Where(p => p.DepartmanRefNavigation != null && p.DepartmanRefNavigation.HotelRef == hotelId);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRefNavigation != null && g.PersonelRefNavigation.DepartmanRefNavigation != null && g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId);
                    departmanQuery = departmanQuery.Where(d => d.HotelRef == hotelId);
                }

                // ⚡ PERFORMANS: personelQuery'de zaten p.AktifMi == 1 koşulu olduğu için mükerrer Where kaldırıldı.
                var departmanPersonelSayilari = await personelQuery
                    .GroupBy(p => p.DepartmanRefNavigation != null ? p.DepartmanRefNavigation.Adi : "Belirtilmemiş")
                    .Select(g => new {
                        departmanAd = g.Key ?? "Belirtilmemiş",
                        adet = g.Count()
                    }).ToListAsync();

                // ⚡ SQL CPU OPTİMİZASYONU: Sadece gerekli kolonları DB'den alıp, hesaplamaları/formatlamayı RAM'e bıraktık.
                var gorevChartDataDb = await gorevQuery
                    .GroupBy(g => new {
                        pId = g.PersonelRef,
                        ad = g.PersonelRefNavigation!.Adi,
                        soyad = g.PersonelRefNavigation.Soyadi,
                        dept = g.PersonelRefNavigation.DepartmanRefNavigation != null ? g.PersonelRefNavigation.DepartmanRefNavigation.Adi : "Belirtilmemiş"
                    })
                    .Select(g => new {
                        pId = g.Key.pId,
                        ad = g.Key.ad,
                        soyad = g.Key.soyad,
                        dept = g.Key.dept,
                        beklemede = g.Count(x => x.Durum == 1),
                        islemde = g.Count(x => x.Durum == 2),
                        tamamlandi = g.Count(x => x.Durum == 3)
                    }).ToListAsync();

                var gorevChartData = gorevChartDataDb.Select(g => new {
                    pId = g.pId,
                    ad = string.IsNullOrWhiteSpace(g.soyad) ? (g.ad ?? string.Empty).Trim() : string.Concat(g.ad, " ", g.soyad).Trim(),
                    dept = g.dept ?? "Belirtilmemiş",
                    beklemede = g.beklemede,
                    islemde = g.islemde,
                    tamamlandi = g.tamamlandi
                }).ToList();

                var departmanlar = await departmanQuery
                    .Select(d => new Departman { Id = d.Id, Adi = d.Adi })
                    .ToListAsync();

                // INDEX DOSTU SORGULAMA: null ve string.Empty kontrolü SQL'in dilinde uygulandı.
                var aiKategoriDb = await gorevQuery
                    .Where(g => g.AiKategori != null && g.AiKategori != "")
                    .GroupBy(g => g.AiKategori)
                    .Select(g => new {
                        kategori = g.Key,
                        adet = g.Count()
                    }).ToListAsync();

                int toplamKategorizeGorev = aiKategoriDb.Sum(x => x.adet);

                // Yüzde hesaplamasını SQL yerine C# RAM'inde yapıyoruz (Çok daha hızlı)
                var aiChartData = aiKategoriDb.Select(x => new {
                    kategori = x.kategori,
                    adet = x.adet,
                    yuzde = toplamKategorizeGorev > 0 ? Math.Round(((double)x.adet / toplamKategorizeGorev) * 100, 1) : 0
                }).OrderByDescending(x => x.yuzde).ToList();

                ViewBag.AiKategoriJson = JsonSerializer.Serialize(aiChartData, _jsonOptions);
                ViewBag.PersonelJson = JsonSerializer.Serialize(departmanPersonelSayilari, _jsonOptions);
                ViewBag.GorevJson = JsonSerializer.Serialize(gorevChartData, _jsonOptions);

                ViewBag.HotelAdi = hotelAdi;
                ViewBag.AktifPersonelAdet = departmanPersonelSayilari.Sum(x => x.adet);
                ViewBag.BeklemedeAdet = gorevChartData.Sum(x => x.beklemede);
                ViewBag.IslemdeAdet = gorevChartData.Sum(x => x.islemde);
                ViewBag.BittiAdet = gorevChartData.Sum(x => x.tamamlandi);
                ViewBag.Departmanlar = departmanlar;

                // Log metodunu UI verilerini bekletmemesi için en sona bıraktık
                await LogKaydetAsync(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", "Dashboard Görüntüleme", hotelAdi, departmanAdi);

                return View();
            }
            catch (Exception)
            {
                // 🛡️ GÜVENLİK: Information Disclosure (Tablo/Ağaç sızıntısı) önlemi
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