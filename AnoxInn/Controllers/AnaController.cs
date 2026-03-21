using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json; // Yüksek Hızlı Yeni Nesil JSON

namespace AxonInn.Controllers
{
    public class AnaController : Controller
    {
        private readonly AxonInnContext _context;

        // ⚡ PERFORMANS 1: JsonSerializerOptions'ı static readonly yaparak her HTTP isteğinde 
        // bellekte (RAM) yeniden üretilmesini engelliyoruz.
        // Ayrıca Javascript grafikleri (Chart.js vb.) ile tam uyum ve daha az ağ trafiği için CamelCase kullanıyoruz.
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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

                // 🛡️ GÜVENLİK 1: Deserialize sonrası verinin bozuk/null gelme ihtimaline karşı bariyer
                if (loginOlanPersonel == null)
                    return RedirectToAction("Login", "Login");

                // ⚡ PERFORMANS 2: Otel Id ve Adı bilgileri ile birlikte Departman adını TEK bağlantı ile çekiyoruz.
                var sessionBilgisi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new {
                        d.HotelRef,
                        HotelAdi = d.HotelRefNavigation.Adi,
                        DepartmanAdi = d.Adi
                    })
                    .FirstOrDefaultAsync();

                // 🛡️ GÜVENLİK 2: Session'ın bozulması veya Nullable (int?) HotelRef'in null gelme ihtimali
                if (sessionBilgisi == null || sessionBilgisi.HotelRef == null || sessionBilgisi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                int hotelId = (int)sessionBilgisi.HotelRef;
                string hotelAdi = sessionBilgisi.HotelAdi ?? "Bilinmeyen Otel";
                string departmanAdi = sessionBilgisi.DepartmanAdi ?? "Bilinmeyen Departman";

                // --- 🚀 PERFORMANS 3: SIFIR GEREKSİZ JOIN (EN BÜYÜK SQL İYİLEŞTİRMESİ) ---
                // Eski kodunuzda Yetki ne olursa olsun önce "HotelRef == hotelId" şartı ekleniyordu.
                // Bu durum, Yetki 2 ve 3'te bile EF Core'un Hotel ve Departman tablolarına gereksiz JOIN atmasına neden oluyordu.
                // Şimdi sorguyu Yetki'ye göre baştan şekillendirerek veritabanı maliyetini minimuma indiriyoruz.

                IQueryable<Personel> personelQuery = _context.Personels.AsNoTracking().Where(p => p.AktifMi == 1);
                IQueryable<Gorev> gorevQuery = _context.Gorevs.AsNoTracking();
                IQueryable<Departman> departmanQuery = _context.Departmen.AsNoTracking();

                if (loginOlanPersonel.Yetki == 3)
                {
                    // YETKİ 3: Sadece kendi verileri (0 Gereksiz JOIN - Direkt ID eşleşmesi)
                    personelQuery = personelQuery.Where(p => p.Id == loginOlanPersonel.Id);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRef == loginOlanPersonel.Id);
                    departmanQuery = departmanQuery.Where(d => d.Id == loginOlanPersonel.DepartmanRef);
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    // YETKİ 2: Sadece kendi departmanının verileri (Hotel tablosuna gitmeyerek JOIN yükü hafifletilir)
                    personelQuery = personelQuery.Where(p => p.DepartmanRef == loginOlanPersonel.DepartmanRef);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRefNavigation.DepartmanRef == loginOlanPersonel.DepartmanRef);
                    departmanQuery = departmanQuery.Where(d => d.Id == loginOlanPersonel.DepartmanRef);
                }
                else
                {
                    // YETKİ 1: Tüm Otel (Mecbur olduğumuz için departman üstünden otele doğru JOIN yapıyoruz)
                    personelQuery = personelQuery.Where(p => p.DepartmanRefNavigation.HotelRef == hotelId);
                    gorevQuery = gorevQuery.Where(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId);
                    departmanQuery = departmanQuery.Where(d => d.HotelRef == hotelId);
                }

                // Grup Sorgusu 1: Departman Personel Sayıları
                var departmanPersonelSayilari = await personelQuery
                    .GroupBy(p => p.DepartmanRefNavigation.Adi)
                    .Select(g => new {
                        departmanAd = g.Key ?? "Belirtilmemiş",
                        adet = g.Count()
                    }).ToListAsync();

                // ⚡ PERFORMANS 4: Metin(String) birleştirme işlemlerini SQL CPU'su yerine C# RAM'ine taşıyoruz.
                // Veritabanı CPU'sunu gereksiz yormamak için SQL'den sadece ham alanları çekiyoruz.
                var gorevChartDataDb = await gorevQuery
                    .GroupBy(g => new {
                        pId = g.PersonelRef,
                        ad = g.PersonelRefNavigation.Adi,
                        soyad = g.PersonelRefNavigation.Soyadi,
                        dept = g.PersonelRefNavigation.DepartmanRefNavigation.Adi
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

                // Ham veriyi Web Sunucusunun belleğinde (RAM) formatlayıp Trimliyoruz. (Veritabanı rahatlar)
                var gorevChartData = gorevChartDataDb.Select(g => new {
                    pId = g.pId,
                    ad = $"{g.ad} {g.soyad}".Trim(),
                    dept = g.dept ?? "Belirtilmemiş",
                    beklemede = g.beklemede,
                    islemde = g.islemde,
                    tamamlandi = g.tamamlandi
                }).ToList();

                var departmanlar = await departmanQuery
                    .Select(d => new Departman { Id = d.Id, Adi = d.Adi })
                    .ToListAsync();

                //GEMİNİ AI KATEGORİ DAĞILIMI HESAPLAMASI////////////////////////////////////////////
                var aiKategoriDb = await gorevQuery
                    .Where(g => !string.IsNullOrEmpty(g.AiKategori))
                    .GroupBy(g => g.AiKategori)
                    .Select(g => new {
                        kategori = g.Key,
                        adet = g.Count()
                    }).ToListAsync();

                int toplamKategorizeGorev = aiKategoriDb.Sum(x => x.adet);

                // Yüzde hesaplamasını RAM'de yapıyoruz (⚡ PERFORMANS)
                var aiChartData = aiKategoriDb.Select(x => new {
                    kategori = x.kategori,
                    adet = x.adet,
                    yuzde = toplamKategorizeGorev > 0 ? Math.Round(((double)x.adet / toplamKategorizeGorev) * 100, 1) : 0
                }).OrderByDescending(x => x.yuzde).ToList();

                // Grafiğe veriyi gönderiyoruz!
                ViewBag.AiKategoriJson = JsonSerializer.Serialize(aiChartData, _jsonOptions);
                //GEMİNİ AI KATEGORİ DAĞILIMI HESAPLAMASI BITTI////////////////////////////////////////////

                // (Değişmedi - Zaten Kusursuzdu) Çekilen özet veriler üzerinden RAM'de toplam sayacı buluyoruz.
                ViewBag.HotelAdi = hotelAdi;
                ViewBag.AktifPersonelAdet = departmanPersonelSayilari.Sum(x => x.adet);
                ViewBag.BeklemedeAdet = gorevChartData.Sum(x => x.beklemede);
                ViewBag.IslemdeAdet = gorevChartData.Sum(x => x.islemde);
                ViewBag.BittiAdet = gorevChartData.Sum(x => x.tamamlandi);

                ViewBag.PersonelJson = JsonSerializer.Serialize(departmanPersonelSayilari, _jsonOptions);
                ViewBag.GorevJson = JsonSerializer.Serialize(gorevChartData, _jsonOptions);
                ViewBag.Departmanlar = departmanlar;

                // ⚡ PERFORMANS 5: Veritabanına INSERT atan (Yazma) Log metodunu sayfa yükleme verilerini (Okuma)
                // bekletmemesi/engellememesi için EN SONA (return View'den hemen önceye) aldık.
                await LogKaydetAsync(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", "Dashboard Görüntüleme", hotelAdi, departmanAdi);

                return View();
            }
            catch (Exception ex)
            {
                // 🛡️ GÜVENLİK 3: ex.Message veritabanı yolları veya tablo isimleri içerebilir (Information Disclosure zafiyeti). 
                // Bunu kullanıcıya açmak tehlikelidir, bu yüzden generic bir hata numarası (TraceIdentifier) fırlatıyoruz.
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        // Metod asenkron olduğu için standartlara uygun olarak isminin sonuna Async eklendi
        private async Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string yeniDeger, string oncedenAlinanHotelAd = "", string oncedenAlinanDepartmanAd = "")
        {
            try
            {
                if (personel == null) return false;

                string departmanAdi = oncedenAlinanDepartmanAd;
                string hotelAdi = oncedenAlinanHotelAd;

                // Controller'dan çağırılırken zaten string gönderdiğimiz için bu DB sorgu bloğu bypass edilerek hız kazanılır.
                if (personel.DepartmanRef != 0 && string.IsNullOrEmpty(hotelAdi))
                {
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, HotelAdi = d.HotelRefNavigation.Adi })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
                        departmanAdi = depBilgisi.Adi ?? "";
                        hotelAdi = depBilgisi.HotelAdi ?? "";
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "SayfaZiyareti",
                    KayitRefId = personel.Id,
                    IslemTipi = islemTipi,
                    EskiDeger = "",
                    YeniDeger = yeniDeger,
                    YapanHotelAd = hotelAdi,
                    YapanDepartmanAd = departmanAdi,
                    YapanAdSoyad = $"{personel.Adi} {personel.Soyadi}".Trim()
                };

                _context.AuditLogs.Add(log);
                await _context.SaveChangesAsync();

                return true;
            }
            catch
            {
                // Log kaydetme işlemi hata alsa dahi projenin ana akışını, dashboard'un açılmasını patlatmamalıdır. (Fail-safe)
                return false;
            }
        }
    }
}