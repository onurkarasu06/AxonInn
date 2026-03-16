using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json; // DEĞİŞİKLİK: Ağır Newtonsoft yerine yüksek hızlı System.Text.Json eklendi

namespace AxonInn.Controllers
{
    public class AnaController : Controller
    {
        private readonly AxonInnContext _context;

        public AnaController(AxonInnContext context)
        {
            _context = context;
        }

        [Route("AnaSayfa")]
        public async Task<IActionResult> Ana()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);
                await LogKaydet(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", "Dashboard Görüntüleme");

                // PERFORMANS 1: Otel Id ve Otel Adı bilgisini 2 ayrı sorgu yerine TEK bir bağlantı (Select) ile çekiyoruz.
                var hotelBilgisi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new { d.HotelRef, HotelAdi = d.HotelRefNavigation.Adi })
                    .FirstOrDefaultAsync();

                if (hotelBilgisi == null || hotelBilgisi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                int hotelId = (int)hotelBilgisi.HotelRef;
                string hotelAdi = hotelBilgisi.HotelAdi;

                // PERFORMANS 2: Sadece okuma yaptığımız için tüm tablolara AsNoTracking eklendi (RAM Tasarrufu).
                var personelChartData = await _context.Personels
                    .AsNoTracking()
                    .Where(p => p.DepartmanRefNavigation.HotelRef == hotelId && p.AktifMi == 1)
                    .Select(p => new {
                        ad = p.Adi,
                        soyad = p.Soyadi,
                        departman = new { ad = p.DepartmanRefNavigation.Adi }
                    }).ToListAsync();

                var gorevChartData = await _context.Gorevs
                    .AsNoTracking()
                    .Where(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId)
                    .Select(g => new {
                        durum = g.Durum,
                        personel = new
                        {
                            id = g.PersonelRef,
                            ad = g.PersonelRefNavigation.Adi,
                            departman = new { ad = g.PersonelRefNavigation.DepartmanRefNavigation.Adi }
                        }
                    }).ToListAsync();

                var departmanlar = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.HotelRef == hotelId)
                    .Select(d => new Departman { Id = d.Id, Adi = d.Adi })
                    .ToListAsync();

                // PERFORMANS 3: Veritabanına 4 kez fazladan CountAsync ile yüklenmek yerine, 
                // Zaten grafikleri çizmek için RAM'e çektiğimiz listelerin eleman sayısını alarak "Sıfır" maliyetle istatistikleri çıkarıyoruz.
                int aktifPersonelAdet = personelChartData.Count;
                int beklemedeAdet = gorevChartData.Count(g => g.durum == 1);
                int islemdeAdet = gorevChartData.Count(g => g.durum == 2);
                int bittiAdet = gorevChartData.Count(g => g.durum == 3);

                ViewBag.HotelAdi = hotelAdi;
                ViewBag.AktifPersonelAdet = aktifPersonelAdet;
                ViewBag.BeklemedeAdet = beklemedeAdet;
                ViewBag.IslemdeAdet = islemdeAdet;
                ViewBag.BittiAdet = bittiAdet;

                // PERFORMANS 4: Daha hafif JSON serileştirme
                ViewBag.PersonelJson = JsonSerializer.Serialize(personelChartData);
                ViewBag.GorevJson = JsonSerializer.Serialize(gorevChartData);
                ViewBag.Departmanlar = departmanlar;

                return View();
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger)
        {
            try
            {
                string departmanAdi = "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    // Diğer sayfalardaki gibi hem departman adını hem de otel adını tek SQL sorgusu ile JOIN yaparak alıyoruz
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, HotelAdi = d.HotelRefNavigation.Adi })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
                        departmanAdi = depBilgisi.Adi;
                        hotelAdi = depBilgisi.HotelAdi;
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "SayfaZiyareti",
                    KayitRefId = personel?.Id ?? 0,
                    IslemTipi = islemTipi,
                    EskiDeger = "",
                    YeniDeger = yeniDeger,
                    YapanHotelAd = hotelAdi,
                    YapanDepartmanAd = departmanAdi,
                    YapanAdSoyad = personel != null ? $"{personel.Adi} {personel.Soyadi}" : "Bilinmeyen"
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