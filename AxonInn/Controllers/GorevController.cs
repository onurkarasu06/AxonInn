using AxonInn.Helpers;
using AxonInn.Models.Analitik;
using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using AxonInn.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization; // ⚡ EKLENDİ: ReferenceHandler için gerekli

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class GorevController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly GeminiApiService _geminiService;
        private readonly ILogService _logService;
        private readonly ICurrentUserService _currentUserService;

        // ⚡ GÜVENLİK VE PERFORMANS: Sonsuz döngü koruması eklendi
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true // ⚡ EKLENDİ: Gelen JSON'daki büyük/küçük harf uyuşmazlığını tolere eder
        };

        // 🛠️ HATA 1 DÜZELTİLDİ: Tüm servisler tek constructor içinde birleştirildi
        public GorevController(AxonInnContext context, GeminiApiService geminiService, ILogService logService, ICurrentUserService currentUserService)
        {
            _context = context;
            _geminiService = geminiService;
            _logService = logService;
            _currentUserService = currentUserService;
        }

        

        [Route("Gorevler")]
        public async Task<IActionResult> Gorev()
        {
            try
            {
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");



                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                var depBilgi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new { d.HotelRef, HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : string.Empty, DepartmanAdi = d.Adi })
                    .FirstOrDefaultAsync();

                if (depBilgi == null || depBilgi.HotelRef == null || depBilgi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

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
                        gorev.GorevFotografs = fotoLookup.Contains(gorev.Id) ? fotoLookup[gorev.Id].ToList() : new List<GorevFotograf>(0);
                    }
                }

                // 🛠️ HATA 2 DÜZELTİLDİ: null yerine string.Empty konuldu ve parametre sırası düzeltildi.
                await _logService.LogKaydetAsync(loginOlanPersonel, "Görev Sayfasına Giriş Yapıldı", string.Empty, "Görev Listesi Görüntüleme", depBilgi.HotelAdi ?? string.Empty, depBilgi.DepartmanAdi ?? string.Empty);

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
                var loginOlanPersonel =  _currentUserService.GetUser();
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
                        if (dosya.Length > 0 && dosya.Length <= 5 * 1024 * 1024 && dosya.IsValidImageSignature())
                        {
                            using var ms = new MemoryStream((int)dosya.Length);
                            await dosya.CopyToAsync(ms);
                            model.GorevFotografs.Add(new GorevFotograf { Fotograf = ms.ToArray() });
                        }
                    }
                }

                try
                {
                    model.AiKategori = await _geminiService.KategorizeEtAsync(model.Aciklama, model.PersonelNotu);
                }
                catch
                {
                    model.AiKategori = "Diğer";
                }

                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    _context.Gorevs.Add(model);
                    await _context.SaveChangesAsync();

                    // 🛠️ HATA 2 DÜZELTİLDİ: Eklenen modeli JSON formatında yeniDeger'e yazıyoruz
                    string jsonModel = JsonSerializer.Serialize(model, _jsonOptions);
                    bool logBasarili = await _logService.LogKaydetAsync(loginOlanPersonel, "Yeni Görev Eklendi", string.Empty, jsonModel);

                    if (!logBasarili) throw new Exception("Log kaydı oluşturulamadı.");

                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }

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
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    await _context.GorevFotografs.Where(f => f.GorevRef == id).ExecuteDeleteAsync();
                    int silinenAdet = await _context.Gorevs.Where(g => g.Id == id).ExecuteDeleteAsync();

                    if (silinenAdet > 0)
                    {
                        // 🛠️ HATA 2 DÜZELTİLDİ: Silinen datanın bilgisini eskiDeger parametresine koyduk, null yerine string.Empty yazdık.
                        bool logBasarili = await _logService.LogKaydetAsync(loginOlanPersonel, "Görev Silindi", $"Görev ID: {id} silindi.", string.Empty);
                        if (!logBasarili) throw new Exception("Log kaydı oluşturulamadı.");
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
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
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

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

                    string? yeniAiKategori = mevcutDurum.AiKategori;
                    if (metinDegisti)
                    {
                        try
                        {
                            yeniAiKategori = await _geminiService.KategorizeEtAsync(yeniAciklama, yeniNot);
                        }
                        catch { /* API Hata verirse eski kategori kalır. */ }
                    }

                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    try
                    {
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

                        if (YeniFotograflar != null && YeniFotograflar.Count > 0)
                        {
                            foreach (var dosya in YeniFotograflar)
                            {
                                if (dosya.Length > 0 && dosya.Length <= 5 * 1024 * 1024 && dosya.IsValidImageSignature())
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
                            await _context.SaveChangesAsync();
                        }

                        var tempPersonel = new Personel { Id = loginOlanPersonel.Id, Adi = loginOlanPersonel.Adi, Soyadi = loginOlanPersonel.Soyadi, DepartmanRef = loginOlanPersonel.DepartmanRef };

                        // 🛠️ HATA 2 DÜZELTİLDİ: Güncellenen yeni modeli JSON formatında yeniDeger alanına basıyoruz.
                        string jsonModel = JsonSerializer.Serialize(model, _jsonOptions);
                        bool logBasarili = await _logService.LogKaydetAsync(tempPersonel, "Görev Güncellendi", $"Güncellenen Görev ID: {model.Id}", jsonModel);

                        if (!logBasarili)
                        {
                            throw new Exception("Log kaydı oluşturulamadığı için işlem geri alınıyor.");
                        }

                        await transaction.CommitAsync();
                    }
                    catch (Exception)
                    {
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
    }
}