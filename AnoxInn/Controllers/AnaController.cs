using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using AxonInn.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization; // ⚡ EKLENDİ: ReferenceHandler için gerekli

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class AnaController : Controller
    {
        private readonly AxonInnContext _context;

        private readonly ILogService _logService;
        private readonly ICurrentUserService _currentUserService;

        // 🛠️ DÜZELTME: Sonsuz döngüleri engelleyen standart ReferenceHandler eklendi
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true // ⚡ EKLENDİ: Gelen JSON'daki büyük/küçük harf uyuşmazlığını tolere eder
        };

        // 🛠️ HATA 1 DÜZELTİLDİ: Tüm servisler tek constructor içinde birleştirildi
        public AnaController(AxonInnContext context, ILogService logService, ICurrentUserService currentUserService)
        {
            _context = context;
            _logService = logService;
            _currentUserService = currentUserService;
        }

        [Route("AnaSayfa")]
        public async Task<IActionResult> Ana()
        {
            try
            {
                var loginOlanPersonel =  _currentUserService.GetUser();

                // ⚡ DÜZELTME 1: Session okunamadıysa önce sil, sonra yönlendir
                if (loginOlanPersonel == null)
                {
                    HttpContext.Session.Remove("GirisYapanPersonel"); // Döngüyü kırar
                    return RedirectToAction("Login", "Login");
                }

                var sessionBilgisi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new {
                        HotelId = d.HotelRef,
                        HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : "Bilinmeyen Otel",
                        DepartmanAdi = d.Adi ?? "Bilinmeyen Departman"
                    })
                    .FirstOrDefaultAsync();

                // ⚡ DÜZELTME 2: Departman bulanamadıysa önce sil, sonra yönlendir
                if (sessionBilgisi == null || sessionBilgisi.HotelId == 0)
                {
                    HttpContext.Session.Remove("GirisYapanPersonel"); // Döngüyü kırar
                    return RedirectToAction("Login", "Login");
                }

                long hotelId = sessionBilgisi.HotelId;

                IQueryable<Personel> personelQuery = _context.Personels.AsNoTracking().Where(p => p.AktifMi == 1);
                IQueryable<Gorev> gorevQuery = _context.Gorevs.AsNoTracking().Where(g => g.PersonelRefNavigation.AktifMi == 1);
                IQueryable<Departman> departmanQuery = _context.Departmen.AsNoTracking();

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

                var aiKategoriDb = await gorevQuery
                    .Where(g => g.AiKategori != null && g.AiKategori != "")
                    .GroupBy(g => g.AiKategori)
                    .Select(g => new { kategori = g.Key, adet = g.Count() })
                    .ToListAsync();

                var departmanlar = await departmanQuery
                    .Select(d => new Departman { Id = d.Id, Adi = d.Adi })
                    .ToListAsync();

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

                ViewBag.AiKategoriJson = JsonSerializer.Serialize(aiChartData, _jsonOptions);
                ViewBag.PersonelJson = JsonSerializer.Serialize(departmanPersonelSayilari, _jsonOptions);
                ViewBag.GorevJson = JsonSerializer.Serialize(gorevChartData, _jsonOptions);

                ViewBag.HotelAdi = sessionBilgisi.HotelAdi;
                ViewBag.AktifPersonelAdet = departmanPersonelSayilari.Sum(x => x.adet);
                ViewBag.BeklemedeAdet = gorevChartData.Sum(x => x.beklemede);
                ViewBag.IslemdeAdet = gorevChartData.Sum(x => x.islemde);
                ViewBag.BittiAdet = gorevChartData.Sum(x => x.tamamlandi);
                ViewBag.Departmanlar = departmanlar;

                // 🛠️ HATA 2 DÜZELTİLDİ: Parametre sıralaması (eskiDeger: string.Empty, yeniDeger: "Dashboard Görüntüleme") olarak düzeltildi.
                await _logService.LogKaydetAsync(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", string.Empty, "Dashboard Görüntüleme", sessionBilgisi.HotelAdi, sessionBilgisi.DepartmanAdi);

                return View();
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }
    }
}