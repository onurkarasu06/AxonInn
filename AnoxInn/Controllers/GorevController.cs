using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.IO;
using System.Diagnostics;

namespace AxonInn.Controllers
{
    public class GorevController : Controller
    {
        private readonly AxonInnContext _context;

        // ⚡ PERFORMANS 1: Her HTTP isteğinde bellekte JSON ayarlarının tekrar oluşturulmasını (Allocation) engeller.
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public GorevController(AxonInnContext context)
        {
            _context = context;
        }

        [Route("Gorevler")]
        public async Task<IActionResult> Gorev()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login"); // Null Crash Koruması

                // ⚡ CPU OPTİMİZASYONU: View (HTML) tarafında tekrar JSON çözümlememek için veriyi aktarıyoruz.
                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                // ⚡ DB OPTİMİZASYONU: Hotel ve Departman adını LogKaydet metodunda tekrar DB'ye gidilmesin diye önceden alıyoruz.
                var depBilgi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new { d.HotelRef, HotelAdi = d.HotelRefNavigation.Adi, DepartmanAdi = d.Adi })
                    .FirstOrDefaultAsync();

                if (depBilgi == null || depBilgi.HotelRef == null || depBilgi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                // 🚀 DRY (Don't Repeat Yourself) PRENSİBİ: Tekrarlayan Include zincirlerini azaltıp sorgu iskeleti kurduk.
                // ⚡ RAM OPTİMİZASYONU: AsSplitQuery korundu. Kartezyen patlamasını önler.
                var query = _context.Hotels
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Where(h => h.Id == depBilgi.HotelRef);

                // --- YETKİ KONTROLÜ VE FİLTRELENMİŞ INCLUDE MANTIĞI ---
                if (loginOlanPersonel.Yetki == 1)
                {
                    query = query.Include(h => h.Departmen)
                                 .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                                 .ThenInclude(p => p.Gorevs);
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                                 .ThenInclude(p => p.Gorevs);
                }
                else if (loginOlanPersonel.Yetki == 3)
                {
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1 && p.Id == loginOlanPersonel.Id))
                                 .ThenInclude(p => p.Gorevs);
                }

                var hotel = await query.FirstOrDefaultAsync();

                if (hotel == null)
                    return RedirectToAction("Login", "Login");

                // ⚡ CPU OPTİMİZASYONU (FLAT-LOOP): İç içe 3 döngü kullanmak yerine, SelectMany ile görevleri düz (flat) bir listeye alıyoruz.
                var tumGorevler = hotel.Departmen.SelectMany(d => d.Personels).SelectMany(p => p.Gorevs).ToList();
                var gorevIds = tumGorevler.Select(g => g.Id).ToList();

                if (gorevIds.Count > 0)
                {
                    // 🚨 SQL IN (2100) ÇÖKME ÖNLEMİ: Dev veri yığınlarında sistemin patlamaması için yazdığınız Chunk (Parçalama) korundu.
                    var fotoIdListesi = new List<GorevFotograf>(gorevIds.Count);
                    foreach (var chunk in gorevIds.Chunk(2000))
                    {
                        var fotolar = await _context.GorevFotografs
                            .AsNoTracking() // Sadece listeleme için Track yapmaya gerek yok.
                            .Where(gf => chunk.Contains(gf.GorevRef))
                            .Select(gf => new GorevFotograf { Id = gf.Id, GorevRef = gf.GorevRef })
                            .ToListAsync();

                        fotoIdListesi.AddRange(fotolar);
                    }

                    // ToLookup ile bellekte O(1) hızında eşleştirme
                    var fotoLookup = fotoIdListesi.ToLookup(f => f.GorevRef);

                    // Düzleştirdiğimiz görev listesi üzerinde tek bir döngü ile İşlemciyi (CPU) rahatlatıyoruz.
                    foreach (var gorev in tumGorevler)
                    {
                        gorev.GorevFotografs = fotoLookup[gorev.Id].ToList();
                    }
                }

                // ⚡ HIZ OPTİMİZASYONU: Veritabanına INSERT atan Log metodunu, Dashboard render hızını kesmemesi için Sona aldık.
                await LogKaydetAsync(loginOlanPersonel, "Görev Sayfasına Giriş Yapıldı", "Görev Listesi Görüntüleme", null, depBilgi.HotelAdi ?? "", depBilgi.DepartmanAdi ?? "");

                return View("Gorev", hotel);
            }
            catch (Exception)
            {
                // 🛡️ GÜVENLİK 1: ex.Message sızdırılması (Information Disclosure Zafiyeti) engellendi.
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Ekle(Gorev model, List<IFormFile>? Fotograf)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                model.KayitTarihi = DateTime.Now;
                model.Durum = 1;

                // ✏️ HATA DÜZELTMESİ: İsim ve soyisim birleştirilirken araya boşluk eklendi.
                if (!string.IsNullOrWhiteSpace(model.Aciklama))
                    model.Aciklama = $"{loginOlanPersonel.Adi} {loginOlanPersonel.Soyadi}:{Environment.NewLine}{model.Aciklama.Trim()}.";

                if (!string.IsNullOrWhiteSpace(model.PersonelNotu))
                    model.PersonelNotu = $"{loginOlanPersonel.Adi} {loginOlanPersonel.Soyadi}:{Environment.NewLine}{model.PersonelNotu.Trim()}.";

                // 🛡️ GÜVENLİK 2: RAM Bombası kalkanı. Sadece resim formatında ve max 5 MB dosyalara izin verilir.
                if (Fotograf != null && Fotograf.Count > 0)
                {
                    model.GorevFotografs = new List<GorevFotograf>(Fotograf.Count);
                    foreach (var dosya in Fotograf)
                    {
                        if (dosya.Length > 0 && dosya.Length <= 5 * 1024 * 1024 && dosya.ContentType.StartsWith("image/"))
                        {
                            // ⚡ CLEAN CODE: C# 8 using deklarasyonu ile gereksiz süslü parantez (scope) karmaşası engellendi
                            using var ms = new MemoryStream((int)dosya.Length);
                            await dosya.CopyToAsync(ms);

                            model.GorevFotografs.Add(new GorevFotograf { Fotograf = ms.ToArray() });
                        }
                    }
                }

                // Entity Framework, Gorev modelini kaydederken içindeki GorevFotografs listesini de 
                // otomatik "Tek Bir Transaction" içerisinde (Bütünlük bozulmadan) kaydeder.
                _context.Gorevs.Add(model);
                await _context.SaveChangesAsync();

                await LogKaydetAsync(loginOlanPersonel, "Yeni Görev Eklendi", $"Görev ID: {model.Id} oluşturuldu.", null);
                return RedirectToAction("Gorev");
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Sil(long id)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                // ⚡ SIFIR RAM TÜKETİMİ (Zero-Memory Delete): Sizin yazdığınız harika yöntem korundu.
                await _context.GorevFotografs.Where(f => f.GorevRef == id).ExecuteDeleteAsync();
                int silinenAdet = await _context.Gorevs.Where(g => g.Id == id).ExecuteDeleteAsync();

                if (silinenAdet > 0)
                {
                    await LogKaydetAsync(loginOlanPersonel, "Görev Silindi", $"Görev ID: {id} silindi.", null);
                }

                return RedirectToAction("Gorev");
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Guncelle(Gorev model, List<IFormFile>? YeniFotograflar)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                var dbGorev = await _context.Gorevs.FindAsync(model.Id);
                if (dbGorev != null)
                {
                    dbGorev.PersonelRef = model.PersonelRef;
                    dbGorev.Aciklama = model.Aciklama;
                    dbGorev.PersonelNotu = model.PersonelNotu;

                    if (dbGorev.Durum != model.Durum)
                    {
                        if (model.Durum == 2 && dbGorev.CozumBaslamaTarihi == null)
                            dbGorev.CozumBaslamaTarihi = DateTime.Now;
                        else if (model.Durum == 3 && dbGorev.CozumBitisTarihi == null)
                            dbGorev.CozumBitisTarihi = DateTime.Now;
                    }

                    dbGorev.Durum = model.Durum;

                    if (YeniFotograflar != null && YeniFotograflar.Count > 0)
                    {
                        foreach (var dosya in YeniFotograflar)
                        {
                            // 🛡️ Aynı 5MB ve Güvenlik (Image) bariyeri güncelleme için de sağlandı.
                            if (dosya.Length > 0 && dosya.Length <= 5 * 1024 * 1024 && dosya.ContentType.StartsWith("image/"))
                            {
                                using var ms = new MemoryStream((int)dosya.Length);
                                await dosya.CopyToAsync(ms);
                                _context.GorevFotografs.Add(new GorevFotograf
                                {
                                    GorevRef = dbGorev.Id,
                                    Fotograf = ms.ToArray()
                                });
                            }
                        }
                    }

                    await _context.SaveChangesAsync();
                    await LogKaydetAsync(loginOlanPersonel, "Görev Güncellendi", $"Görev ID: {dbGorev.Id} güncellendi.", null);
                }

                return RedirectToAction("Gorev");
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpGet]
        [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> PersonelFotoGetir(long id)
        {
            var fotoBytes = await _context.PersonelFotografs
                .AsNoTracking()
                .Where(f => f.PersonelRef == id)
                .Select(f => f.Fotograf)
                .FirstOrDefaultAsync();

            // C# 9.0+ Pattern Matching ile şık null ve boyut kontrolü
            if (fotoBytes is { Length: > 0 })
                return File(fotoBytes, "image/jpeg");

            return NotFound();
        }

        [HttpGet]
        [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> KanitFotoGetir(long id)
        {
            var fotoBytes = await _context.GorevFotografs
                .AsNoTracking()
                .Where(f => f.Id == id)
                .Select(f => f.Fotograf)
                .FirstOrDefaultAsync();

            if (fotoBytes is { Length: > 0 })
                return File(fotoBytes, "image/jpeg");

            return NotFound();
        }

        // Asenkron olduğu için Standartlara uygun olarak sonuna Async eklendi.
        private async Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string yeniDeger, Gorev? gorev, string preHotelAdi = "", string preDeptAdi = "")
        {
            try
            {
                if (personel == null) return false;

                string departmanAdi = !string.IsNullOrEmpty(preDeptAdi) ? preDeptAdi : (personel.DepartmanRefNavigation?.Adi ?? "");
                string hotelAdi = preHotelAdi;

                if (personel.DepartmanRef != 0 && string.IsNullOrEmpty(hotelAdi))
                {
                    var data = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, hAdi = d.HotelRefNavigation.Adi })
                        .FirstOrDefaultAsync();

                    if (data != null)
                    {
                        hotelAdi = data.hAdi ?? "";
                        departmanAdi = data.Adi ?? "";
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Gorev",
                    KayitRefId = gorev?.Id ?? personel.Id,
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
                return false;
            }
        }
    }
}