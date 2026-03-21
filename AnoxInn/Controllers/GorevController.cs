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
        private readonly GeminiApiService _geminiService;

        // ⚡ PERFORMANS: Her HTTP isteğinde bellekte JSON ayarlarının tekrar oluşturulmasını (GC yükünü) engeller.
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // 👈 YENİ: GeminiApiService'i parametre olarak içeri alıyoruz
        public GorevController(AxonInnContext context, GeminiApiService geminiService)
        {
            _context = context;
            _geminiService = geminiService; // 👈 YENİ: Atamasını yapıyoruz
        }

      

        // ⚡ PERFORMANS: Session okuma ve Deserialize işlemini merkezileştirerek bellek tüketimini azalttık.
        private Personel? GetGirisYapanPersonel()
        {
            var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
            return string.IsNullOrEmpty(personelJson) ? null : JsonSerializer.Deserialize<Personel>(personelJson, _jsonOptions);
        }

        [Route("Gorevler")]
        public async Task<IActionResult> Gorev()
        {
            try
            {
                var loginOlanPersonel = GetGirisYapanPersonel();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                var depBilgi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new { d.HotelRef, HotelAdi = d.HotelRefNavigation.Adi, DepartmanAdi = d.Adi })
                    .FirstOrDefaultAsync();

                if (depBilgi?.HotelRef == null || depBilgi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                // ⚡ PERFORMANS: AsNoTrackingWithIdentityResolution eklendi.
                // Include zincirlerinde aynı departman nesnesinin bellekte defalarca kopyalanmasını engelleyerek RAM tüketimini büyük ölçüde düşürür.
                var query = _context.Hotels
                    .AsNoTrackingWithIdentityResolution()
                    .AsSplitQuery()
                    .Where(h => h.Id == depBilgi.HotelRef);

                if (loginOlanPersonel.Yetki == 1)
                {
                    query = query.Include(h => h.Departmen)
                                 .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                                 .ThenInclude(p => p.Gorevs.OrderByDescending(g => g.Id));
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                                 .ThenInclude(p => p.Gorevs.OrderByDescending(g => g.Id));
                }
                else if (loginOlanPersonel.Yetki == 3)
                {
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1 && p.Id == loginOlanPersonel.Id))
                                 .ThenInclude(p => p.Gorevs.OrderByDescending(g => g.Id));
                }

                var hotel = await query.FirstOrDefaultAsync();
                if (hotel == null) return RedirectToAction("Login", "Login");

                var tumGorevler = hotel.Departmen.SelectMany(d => d.Personels).SelectMany(p => p.Gorevs).ToList();
                var gorevIds = tumGorevler.Select(g => g.Id).ToList();

                if (gorevIds.Count > 0)
                {
                    var fotoIdListesi = new List<GorevFotograf>(gorevIds.Count);

                    foreach (var chunk in gorevIds.Chunk(2000))
                    {
                        // ⚡ PERFORMANS: Anonim tip çekerek Entity Tracking ve Materialization bellekteki yükünü hafifletiyoruz
                        var fotolar = await _context.GorevFotografs
                            .AsNoTracking()
                            .Where(gf => chunk.Contains(gf.GorevRef))
                            .Select(gf => new { gf.Id, gf.GorevRef })
                            .ToListAsync();

                        fotoIdListesi.AddRange(fotolar.Select(f => new GorevFotograf { Id = f.Id, GorevRef = f.GorevRef }));
                    }

                    var fotoLookup = fotoIdListesi.ToLookup(f => f.GorevRef);

                    foreach (var gorev in tumGorevler)
                    {
                        gorev.GorevFotografs = fotoLookup[gorev.Id].ToList();
                    }
                }

                await LogKaydetAsync(loginOlanPersonel, "Görev Sayfasına Giriş Yapıldı", "Görev Listesi Görüntüleme", null, depBilgi.HotelAdi ?? "", depBilgi.DepartmanAdi ?? "");

                return View("Gorev", hotel);
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Ekle(Gorev model, List<IFormFile>? Fotograf)
        {
            try
            {
                var loginOlanPersonel = GetGirisYapanPersonel();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                model.KayitTarihi = DateTime.Now;
                model.Durum = 1;

                string logPrefix = $"{loginOlanPersonel.Adi} {loginOlanPersonel.Soyadi} ({model.KayitTarihi:dd.MM.yyyy HH:mm}):{Environment.NewLine}";

                if (!string.IsNullOrWhiteSpace(model.Aciklama))
                    model.Aciklama = $"{logPrefix}{model.Aciklama.Trim()}.";

                if (!string.IsNullOrWhiteSpace(model.PersonelNotu))
                    model.PersonelNotu = $"{logPrefix}{model.PersonelNotu.Trim()}.";

                if (Fotograf != null && Fotograf.Count > 0)
                {
                    model.GorevFotografs = new List<GorevFotograf>(Fotograf.Count);
                    foreach (var dosya in Fotograf)
                    {
                        if (dosya.Length > 0 && dosya.Length <= 5 * 1024 * 1024 && dosya.ContentType.StartsWith("image/"))
                        {
                            using var ms = new MemoryStream((int)dosya.Length); // ⚡ PERFORMANS: Başlangıç kapasitesi belirtilerek RAM korundu
                            await dosya.CopyToAsync(ms);

                            model.GorevFotografs.Add(new GorevFotograf { Fotograf = ms.ToArray() });
                        }
                    }
                }

                // 🤖 YENİ: Veritabanına kaydetmeden SADECE BİR SATIR ÖNCE Gemini'ye soruyoruz
                model.AiKategori = await _geminiService.KategorizeEtAsync(model.Aciklama, model.PersonelNotu);

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
                var loginOlanPersonel = GetGirisYapanPersonel();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

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

        // ⚡ YARDIMCI METOT: String temizleme işlemlerini hızlandırmak için
        private string NormalizeText(string? input) => (input ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

        [HttpPost]
        public async Task<IActionResult> Guncelle(Gorev model, List<IFormFile>? YeniFotograflar)
        {
            try
            {
                var loginOlanPersonel = GetGirisYapanPersonel();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                var dbGorev = await _context.Gorevs.FindAsync(model.Id);
                if (dbGorev != null)
                {
                    bool metinDegisti = false;

                    if (model.PersonelRef > 0)
                    {
                        dbGorev.PersonelRef = model.PersonelRef;
                    }

                    string logPrefix = $"{loginOlanPersonel.Adi} {loginOlanPersonel.Soyadi} ({DateTime.Now:dd.MM.yyyy HH:mm}):{Environment.NewLine}";

                    if (dbGorev.Aciklama != model.Aciklama)
                    {
                        metinDegisti = true;
                        string eskiMetin = NormalizeText(dbGorev.Aciklama);
                        string yeniMetin = NormalizeText(model.Aciklama);

                        if (yeniMetin != eskiMetin)
                        {
                            if (eskiMetin.Length > 0 && yeniMetin.StartsWith(eskiMetin))
                            {
                                string sadeceYeniYazi = yeniMetin.Substring(eskiMetin.Length).Trim();
                                if (!string.IsNullOrEmpty(sadeceYeniYazi))
                                {
                                    dbGorev.Aciklama = $"{dbGorev.Aciklama}{Environment.NewLine}{logPrefix}{sadeceYeniYazi}.";
                                }
                            }
                            else
                            {
                                dbGorev.Aciklama = model.Aciklama;
                            }
                        }
                    }

                    if (dbGorev.PersonelNotu != model.PersonelNotu)
                    {
                        metinDegisti = true;
                        string eskiNot = NormalizeText(dbGorev.PersonelNotu);
                        string yeniNot = NormalizeText(model.PersonelNotu);

                        if (yeniNot != eskiNot)
                        {
                            if (eskiNot.Length > 0 && yeniNot.StartsWith(eskiNot))
                            {
                                string sadeceYeniNot = yeniNot.Substring(eskiNot.Length).Trim();
                                if (!string.IsNullOrEmpty(sadeceYeniNot))
                                {
                                    string ayirici = string.IsNullOrEmpty(dbGorev.PersonelNotu) ? "" : Environment.NewLine;
                                    dbGorev.PersonelNotu = $"{dbGorev.PersonelNotu}{ayirici}{logPrefix}{sadeceYeniNot}.";
                                }
                            }
                            else if (eskiNot.Length == 0 && yeniNot.Length > 0)
                            {
                                dbGorev.PersonelNotu = $"{logPrefix}{yeniNot.Trim()}.";
                            }
                            else
                            {
                                dbGorev.PersonelNotu = model.PersonelNotu;
                            }
                        }
                    }

                    if (dbGorev.Durum != model.Durum && model.Durum > 0)
                    {
                        if (model.Durum == 2 && dbGorev.CozumBaslamaTarihi == null)
                            dbGorev.CozumBaslamaTarihi = DateTime.Now;
                        else if (model.Durum == 3 && dbGorev.CozumBitisTarihi == null)
                            dbGorev.CozumBitisTarihi = DateTime.Now;

                        dbGorev.Durum = model.Durum;
                    }

                    if (YeniFotograflar != null && YeniFotograflar.Count > 0)
                    {
                        foreach (var dosya in YeniFotograflar)
                        {
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

                    if (metinDegisti)
                    {
                        dbGorev.AiKategori = await _geminiService.KategorizeEtAsync(dbGorev.Aciklama, dbGorev.PersonelNotu);
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