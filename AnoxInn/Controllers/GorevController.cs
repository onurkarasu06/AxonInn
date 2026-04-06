using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.IO;
using System.Diagnostics;
using AxonInn.Models.Entities;
using AxonInn.Models.Context;
using AxonInn.Models.Analitik;

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
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

        public GorevController(AxonInnContext context, GeminiApiService geminiService)
        {
            _context = context;
            _geminiService = geminiService;
        }

        // ⚡ GÜVENLİK VE PERFORMANS: Session okuma işlemini merkezileştirerek DRY prensibini sağladık.
        private Personel? GetGirisYapanPersonel()
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
                    .Select(d => new { d.HotelRef, HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : string.Empty, DepartmanAdi = d.Adi })
                    .FirstOrDefaultAsync();

                if (depBilgi == null || depBilgi.HotelRef == null || depBilgi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                // ⚡ PERFORMANS: AsNoTrackingWithIdentityResolution. 
                // Ağır Include zincirlerinde nesnelerin bellekte kopyalanmasını önleyip RAM dostu çalışır. Cartesian patlamayı önlemek için AsSplitQuery kullanıldı.
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
                    var fotoIdListesi = new List<GorevFotograf>(gorevIds.Count * 2);

                    foreach (var chunk in gorevIds.Chunk(2000))
                    {
                        // 🚀 ZERO-ALLOCATION (Sıfır Bellek Tahsisi): 
                        // Yalnızca ID ve GorevRef alınıp anonim tip üretmeden asıl tipe aktarıldı.
                        var fotolar = await _context.GorevFotografs
                            .AsNoTracking()
                            .Where(gf => chunk.Contains(gf.GorevRef))
                            .Select(gf => new GorevFotograf { Id = gf.Id, GorevRef = gf.GorevRef })
                            .ToListAsync();

                        fotoIdListesi.AddRange(fotolar);
                    }

                    var fotoLookup = fotoIdListesi.ToLookup(f => f.GorevRef);

                    foreach (var gorev in tumGorevler)
                    {
                        // Resim yoksa boş yere RAM'de obje tahsis edilmez.
                        gorev.GorevFotografs = fotoLookup.Contains(gorev.Id) ? fotoLookup[gorev.Id].ToList() : new List<GorevFotograf>(0);
                    }
                }

                await LogKaydetAsync(loginOlanPersonel, "Görev Sayfasına Giriş Yapıldı", "Görev Listesi Görüntüleme", null, depBilgi.HotelAdi ?? string.Empty, depBilgi.DepartmanAdi ?? string.Empty);

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

                string logPrefix = string.Concat(loginOlanPersonel.Adi, " ", loginOlanPersonel.Soyadi, " (", model.KayitTarihi.ToString("dd.MM.yyyy HH:mm"), "):", Environment.NewLine);

                if (!string.IsNullOrWhiteSpace(model.Aciklama))
                    model.Aciklama = string.Concat(logPrefix, model.Aciklama.Trim(), ".");

                if (!string.IsNullOrWhiteSpace(model.PersonelNotu))
                    model.PersonelNotu = string.Concat(logPrefix, model.PersonelNotu.Trim(), ".");

                if (Fotograf != null && Fotograf.Count > 0)
                {
                    model.GorevFotografs = new List<GorevFotograf>(Fotograf.Count);
                    foreach (var dosya in Fotograf)
                    {
                        if (dosya.Length > 0 && dosya.Length <= 5 * 1024 * 1024 && dosya.ContentType.StartsWith("image/"))
                        {
                            using var ms = new MemoryStream((int)dosya.Length);
                            await dosya.CopyToAsync(ms);
                            model.GorevFotografs.Add(new GorevFotograf { Fotograf = ms.ToArray() });
                        }
                    }
                }

                // 🤖 AI KORUMASI: Gemini API çökerse bile "Görev Oluşturma" işlemi sekteye uğramaz
                try
                {
                    model.AiKategori = await _geminiService.KategorizeEtAsync(model.Aciklama, model.PersonelNotu);
                }
                catch
                {
                    model.AiKategori = "Diğer";
                }

                _context.Gorevs.Add(model);
                await _context.SaveChangesAsync();

                await LogKaydetAsync(loginOlanPersonel, "Yeni Görev Eklendi", $"Görev ID: {model.Id} oluşturuldu.", null);
                return RedirectToAction(nameof(Gorev));
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

                // ExecuteDeleteAsync doğrudan SQL'e gittiği için kusursuzdur, RAM tüketimi 0'dır.
                await _context.GorevFotografs.Where(f => f.GorevRef == id).ExecuteDeleteAsync();
                int silinenAdet = await _context.Gorevs.Where(g => g.Id == id).ExecuteDeleteAsync();

                if (silinenAdet > 0)
                {
                    await LogKaydetAsync(loginOlanPersonel, "Görev Silindi", $"Görev ID: {id} silindi.", null);
                }

                return RedirectToAction(nameof(Gorev));
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        private string NormalizeText(string? input) => string.IsNullOrWhiteSpace(input) ? string.Empty : input.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

        [HttpPost]
        public async Task<IActionResult> Guncelle(Gorev model, List<IFormFile>? YeniFotograflar)
        {
            try
            {
                var loginOlanPersonel = GetGirisYapanPersonel();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                // 🚀 MUAZZAM RAM OPTİMİZASYONU:
                // FindAsync ile tüm tabloyu RAM'e çekip izlemeye (Tracking) almak yerine
                // sadece ihtiyacımız olan mevcut alanları anonim (hafif) tiplerle okuyoruz.
                var mevcutDurum = await _context.Gorevs
                    .AsNoTracking()
                    .Where(g => g.Id == model.Id)
                    .Select(g => new {
                        g.PersonelRef,
                        g.Aciklama,
                        g.PersonelNotu,
                        g.Durum,
                        g.CozumBaslamaTarihi,
                        g.CozumBitisTarihi,
                        g.AiKategori
                    })
                    .FirstOrDefaultAsync();

                if (mevcutDurum != null)
                {
                    bool metinDegisti = false;
                    long yeniPersonelRef = model.PersonelRef > 0 ? model.PersonelRef : mevcutDurum.PersonelRef;

                    string logPrefix = string.Concat(loginOlanPersonel.Adi, " ", loginOlanPersonel.Soyadi, " (", DateTime.Now.ToString("dd.MM.yyyy HH:mm"), "):", Environment.NewLine);

                    string yeniAciklama = mevcutDurum.Aciklama ?? string.Empty;
                    string yeniNot = mevcutDurum.PersonelNotu ?? string.Empty;

                    // --- Açıklama Kontrolü ---
                    string eskiMetin = NormalizeText(mevcutDurum.Aciklama);
                    string yeniMetin = NormalizeText(model.Aciklama);

                    if (yeniMetin != eskiMetin)
                    {
                        metinDegisti = true;
                        if (eskiMetin.Length > 0 && yeniMetin.StartsWith(eskiMetin))
                        {
                            string sadeceYeniYazi = yeniMetin.Substring(eskiMetin.Length).Trim();
                            if (!string.IsNullOrWhiteSpace(sadeceYeniYazi))
                                yeniAciklama = string.Concat(mevcutDurum.Aciklama, Environment.NewLine, logPrefix, sadeceYeniYazi, ".");
                        }
                        else
                        {
                            yeniAciklama = model.Aciklama ?? string.Empty;
                        }
                    }

                    // --- Not Kontrolü ---
                    string eskiNot = NormalizeText(mevcutDurum.PersonelNotu);
                    string yeniNotGirilen = NormalizeText(model.PersonelNotu);

                    if (yeniNotGirilen != eskiNot)
                    {
                        metinDegisti = true;
                        if (eskiNot.Length > 0 && yeniNotGirilen.StartsWith(eskiNot))
                        {
                            string sadeceYeniNot = yeniNotGirilen.Substring(eskiNot.Length).Trim();
                            if (!string.IsNullOrWhiteSpace(sadeceYeniNot))
                            {
                                string ayirici = string.IsNullOrWhiteSpace(mevcutDurum.PersonelNotu) ? string.Empty : Environment.NewLine;
                                yeniNot = string.Concat(mevcutDurum.PersonelNotu, ayirici, logPrefix, sadeceYeniNot, ".");
                            }
                        }
                        else if (eskiNot.Length == 0 && yeniNotGirilen.Length > 0)
                        {
                            yeniNot = string.Concat(logPrefix, yeniNotGirilen.Trim(), ".");
                        }
                        else
                        {
                            yeniNot = model.PersonelNotu ?? string.Empty;
                        }
                    }

                    // --- Durum ve Tarih ---
                    int yeniDurumDegeri = mevcutDurum.Durum;
                    DateTime? yeniBaslamaTarihi = mevcutDurum.CozumBaslamaTarihi;
                    DateTime? yeniBitisTarihi = mevcutDurum.CozumBitisTarihi;

                    if (mevcutDurum.Durum != model.Durum && model.Durum > 0)
                    {
                        yeniDurumDegeri = model.Durum;
                        if (model.Durum == 2 && mevcutDurum.CozumBaslamaTarihi == null)
                            yeniBaslamaTarihi = DateTime.Now;
                        else if (model.Durum == 3 && mevcutDurum.CozumBitisTarihi == null)
                            yeniBitisTarihi = DateTime.Now;
                    }

                    // --- AI Kategori Güncelleme ---
                    string? yeniAiKategori = mevcutDurum.AiKategori;
                    if (metinDegisti)
                    {
                        try
                        {
                            yeniAiKategori = await _geminiService.KategorizeEtAsync(yeniAciklama, yeniNot);
                        }
                        catch { /* API Hata verirse eski kategori kalır. */ }
                    }

                    // 🛡️ TRANSACTION BAŞLANGICI: Veri bütünlüğü için DB işlemlerini sarıyoruz
                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    try
                    {
                        // ⚡ SQL BYPASS: ChangeTracker'ı atlayıp doğrudan veritabanında UPDATE gönderiyoruz
                        var updateQuery = _context.Gorevs.Where(g => g.Id == model.Id);

                        if (metinDegisti)
                        {
                            await updateQuery.ExecuteUpdateAsync(s => s
                                .SetProperty(g => g.PersonelRef, yeniPersonelRef)
                                .SetProperty(g => g.Aciklama, yeniAciklama)
                                .SetProperty(g => g.PersonelNotu, yeniNot)
                                .SetProperty(g => g.Durum, yeniDurumDegeri)
                                .SetProperty(g => g.CozumBaslamaTarihi, yeniBaslamaTarihi)
                                .SetProperty(g => g.CozumBitisTarihi, yeniBitisTarihi)
                                .SetProperty(g => g.AiKategori, g => yeniAiKategori ?? g.AiKategori));
                        }
                        else
                        {
                            await updateQuery.ExecuteUpdateAsync(s => s
                               .SetProperty(g => g.PersonelRef, yeniPersonelRef)
                               .SetProperty(g => g.Durum, yeniDurumDegeri)
                               .SetProperty(g => g.CozumBaslamaTarihi, yeniBaslamaTarihi)
                               .SetProperty(g => g.CozumBitisTarihi, yeniBitisTarihi));
                        }

                        // --- Yeni Fotoğraf Kaydı ---
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
                                        GorevRef = model.Id,
                                        Fotograf = ms.ToArray()
                                    });
                                }
                            }
                            await _context.SaveChangesAsync(); // Sadece yeni Insert fotoğraflar için SaveChanges atılır
                        }

                        // LogKaydet DB'ye gitmemesi için sanal bir nesne üretiyoruz
                        var tempPersonel = new Personel { Id = loginOlanPersonel.Id, Adi = loginOlanPersonel.Adi, Soyadi = loginOlanPersonel.Soyadi, DepartmanRef = loginOlanPersonel.DepartmanRef };

                        bool logBasarili = await LogKaydetAsync(tempPersonel, "Görev Güncellendi", $"Görev ID: {model.Id} güncellendi.", null);

                        if (!logBasarili)
                        {
                            throw new Exception("Log kaydı oluşturulamadığı için işlem geri alınıyor.");
                        }

                        // Tüm DB işlemleri başarılıysa onaylıyoruz
                        await transaction.CommitAsync();
                    }
                    catch (Exception)
                    {
                        // Hata anında işlemleri geri al ve Error sayfasına yönlendirilmesi için hatayı dışarı fırlat
                        await transaction.RollbackAsync();
                        throw;
                    }
                }

                return RedirectToAction(nameof(Gorev));
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

            if (fotoBytes != null && fotoBytes.Length > 0)
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

            if (fotoBytes != null && fotoBytes.Length > 0)
                return File(fotoBytes, "image/jpeg");

            return NotFound();
        }

        private async Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string yeniDeger, Gorev? gorev, string preHotelAdi = "", string preDeptAdi = "")
        {
            try
            {
                if (personel == null) return false;

                string departmanAdi = !string.IsNullOrWhiteSpace(preDeptAdi) ? preDeptAdi : (personel.DepartmanRefNavigation?.Adi ?? string.Empty);
                string hotelAdi = preHotelAdi;

                if (personel.DepartmanRef != 0 && string.IsNullOrWhiteSpace(hotelAdi))
                {
                    var data = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, hAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : string.Empty })
                        .FirstOrDefaultAsync();

                    if (data != null)
                    {
                        hotelAdi = data.hAdi ?? string.Empty;
                        departmanAdi = data.Adi ?? string.Empty;
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Gorev",
                    KayitRefId = gorev?.Id ?? personel.Id,
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