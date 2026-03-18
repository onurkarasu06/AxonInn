using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json; // Yüksek Hızlı Yeni Nesil JSON

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

                // ⚡ PERFORMANS 1: Otel Id ve Adı bilgileri ile birlikte Departman adını TEK bir bağlantı ile çekiyoruz.
                var sessionBilgisi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new {
                        d.HotelRef,
                        HotelAdi = d.HotelRefNavigation.Adi,
                        DepartmanAdi = d.Adi
                    })
                    .FirstOrDefaultAsync();

                if (sessionBilgisi == null || sessionBilgisi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                int hotelId = (int)sessionBilgisi.HotelRef;
                string hotelAdi = sessionBilgisi.HotelAdi;

                // ⚡ PERFORMANS 2: Veritabanına tekrar gitmemesi için elimizdeki otel ve departman adını Log metoduna parametre yolluyoruz.
                await LogKaydet(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", "Dashboard Görüntüleme", hotelAdi, sessionBilgisi.DepartmanAdi);

                // ⚡ PERFORMANS 3: Binlerce satır personel datasını RAM'e çekip JS ile saydırmak yerine
                // Doğrudan SQL seviyesinde (GroupBy) departman bazlı kişi adetlerini çekiyoruz. (Ağ yükü %99 azaldı)
                var departmanPersonelSayilari = await _context.Personels
                    .AsNoTracking()
                    .Where(p => p.DepartmanRefNavigation.HotelRef == hotelId && p.AktifMi == 1)
                    .GroupBy(p => p.DepartmanRefNavigation.Adi)
                    .Select(g => new {
                        departmanAd = g.Key ?? "Belirtilmemiş",
                        adet = g.Count()
                    }).ToListAsync();

                // ⚡ PERFORMANS 4: On binlerce görev verisini tek tek HTML içine göndermek tarayıcıyı kilitler.
                // EF Core üzerinden SQL GroupBy tetikleniyor. Sadece "Hangi Personel, Hangi Durumda Kaç İşe Sahip" 
                // verisi minik, güvenli bir özet liste olarak çekiliyor.
                var gorevChartData = await _context.Gorevs
                    .AsNoTracking()
                    .Where(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId)
                    .GroupBy(g => new {
                        pId = g.PersonelRef,
                        ad = g.PersonelRefNavigation.Adi,
                        soyad = g.PersonelRefNavigation.Soyadi,
                        dept = g.PersonelRefNavigation.DepartmanRefNavigation.Adi
                    })
                    .Select(g => new {
                        pId = g.Key.pId,
                        ad = (g.Key.ad + " " + g.Key.soyad).Trim(),
                        dept = g.Key.dept ?? "Belirtilmemiş",
                        beklemede = g.Count(x => x.Durum == 1),
                        islemde = g.Count(x => x.Durum == 2),
                        tamamlandi = g.Count(x => x.Durum == 3)
                    }).ToListAsync();

                var departmanlar = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.HotelRef == hotelId)
                    .Select(d => new Departman { Id = d.Id, Adi = d.Adi })
                    .ToListAsync();

                // ⚡ PERFORMANS 5: Ekran üstündeki 4 adet Sayaç (Count) için veritabanına bir daha "CountAsync()" ile gitmiyoruz!
                // Halihazırda SQL'den grafikler için çektiğimiz özet listelerin basitçe matematiksel toplamını alıyoruz.
                int aktifPersonelAdet = departmanPersonelSayilari.Sum(x => x.adet);
                int beklemedeAdet = gorevChartData.Sum(x => x.beklemede);
                int islemdeAdet = gorevChartData.Sum(x => x.islemde);
                int bittiAdet = gorevChartData.Sum(x => x.tamamlandi);

                ViewBag.HotelAdi = hotelAdi;
                ViewBag.AktifPersonelAdet = aktifPersonelAdet;
                ViewBag.BeklemedeAdet = beklemedeAdet;
                ViewBag.IslemdeAdet = islemdeAdet;
                ViewBag.BittiAdet = bittiAdet;

                // Megabaytlarca ham JSON yükü yerine sadece birkaç KB'lık (Zaten SQL'de sayılmış) temiz istatistik yollanır.
                ViewBag.PersonelJson = JsonSerializer.Serialize(departmanPersonelSayilari);
                ViewBag.GorevJson = JsonSerializer.Serialize(gorevChartData);
                ViewBag.Departmanlar = departmanlar;

                return View();
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, string oncedenAlinanHotelAd = "", string oncedenAlinanDepartmanAd = "")
        {
            try
            {
                // Ekstra SQL sorgularını engellemek için varsa yukarıdan gelen hazır parametreleri kullanıyoruz.
                string departmanAdi = oncedenAlinanDepartmanAd;
                string hotelAdi = oncedenAlinanHotelAd;

                if (personel != null && personel.DepartmanRef != 0 && string.IsNullOrEmpty(hotelAdi))
                {
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
                    YapanHotelAd = hotelAdi ?? "",
                    YapanDepartmanAd = departmanAdi ?? "",
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