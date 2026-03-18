using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.IO;

namespace AxonInn.Controllers
{
    public class GorevController : Controller
    {
        private readonly AxonInnContext _context;

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

                // ⚡ CPU OPTİMİZASYONU: View (HTML) tarafında tekrar JSON çözümlememek için veriyi aktarıyoruz.
                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                // ⚡ DB OPTİMİZASYONU: Hotel ve Departman adını ilk adımda alıyoruz ki LogKaydet metodunda tekrar DB'ye gidilmesin.
                var depBilgi = await _context.Departmen
                    .AsNoTracking()
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => new { d.HotelRef, HotelAdi = d.HotelRefNavigation.Adi, DepartmanAdi = d.Adi })
                    .FirstOrDefaultAsync();

                if (depBilgi == null || depBilgi.HotelRef == 0)
                    return RedirectToAction("Login", "Login");

                await LogKaydet(loginOlanPersonel, "Görev Sayfasına Giriş Yapıldı", "Görev Listesi Görüntüleme", null, depBilgi.HotelAdi, depBilgi.DepartmanAdi);

                // ⚡ RAM OPTİMİZASYONU: AsSplitQuery korundu. Kartezyen patlamasını önler.
                var hotel = await _context.Hotels
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(h => h.Departmen)
                        .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                            .ThenInclude(p => p.Gorevs)
                    .FirstOrDefaultAsync(h => h.Id == depBilgi.HotelRef);

                if (hotel == null)
                    return RedirectToAction("Login", "Login");

                var gorevIds = hotel.Departmen.SelectMany(d => d.Personels).SelectMany(p => p.Gorevs).Select(g => g.Id).ToList();

                if (gorevIds.Count > 0)
                {
                    // 🚨 SQL IN (2100) ÇÖKME ÖNLEMİ: Dev veri yığınlarında sistemin patlamaması için Chunk (Parçalama) kullanıldı.
                    var fotoIdListesi = new List<GorevFotograf>();
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

                    foreach (var departman in hotel.Departmen)
                    {
                        foreach (var personel in departman.Personels)
                        {
                            foreach (var gorev in personel.Gorevs)
                            {
                                gorev.GorevFotografs = fotoLookup[gorev.Id].ToList();
                            }
                        }
                    }
                }

                return View("Gorev", hotel);
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Ekle(Gorev model, List<IFormFile> Fotograf)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                model.KayitTarihi = DateTime.Now;
                model.Durum = 1;

                // ⚡ NETWORK OPTİMİZASYONU: Fotoğraflar baştan göreve bağlanıp veritabanına tek seferde gönderiliyor.
                if (Fotograf != null && Fotograf.Count > 0)
                {
                    model.GorevFotografs = new List<GorevFotograf>(Fotograf.Count);
                    foreach (var dosya in Fotograf)
                    {
                        if (dosya.Length > 0)
                        {
                            // RAM bellek sızıntısını önlemek için uzunluk baştan verildi.
                            using (var ms = new MemoryStream((int)dosya.Length))
                            {
                                await dosya.CopyToAsync(ms);
                                model.GorevFotografs.Add(new GorevFotograf
                                {
                                    Fotograf = ms.ToArray()
                                });
                            }
                        }
                    }
                }

                _context.Gorevs.Add(model);
                await _context.SaveChangesAsync();

                await LogKaydet(loginOlanPersonel, "Yeni Görev Eklendi", $"Görev ID: {model.Id} oluşturuldu.", null);
                return RedirectToAction("Gorev");
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
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

                // ⚡ SIFIR RAM TÜKETİMİ (Zero-Memory Delete): Veriyi RAM'e çekmek (FindAsync) yerine SQL üzerinden ışık hızında siliyoruz.
                await _context.GorevFotografs.Where(f => f.GorevRef == id).ExecuteDeleteAsync();
                int silinenAdet = await _context.Gorevs.Where(g => g.Id == id).ExecuteDeleteAsync();

                if (silinenAdet > 0)
                {
                    await LogKaydet(loginOlanPersonel, "Görev Silindi", $"Görev ID: {id} silindi.", null);
                }

                return RedirectToAction("Gorev");
            }
            catch (Exception ex)
            {
                string gercekHata = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = gercekHata });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Guncelle(Gorev model, List<IFormFile> YeniFotograflar)
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
                            if (dosya.Length > 0)
                            {
                                using (var ms = new MemoryStream((int)dosya.Length))
                                {
                                    await dosya.CopyToAsync(ms);
                                    _context.GorevFotografs.Add(new GorevFotograf
                                    {
                                        GorevRef = dbGorev.Id,
                                        Fotograf = ms.ToArray()
                                    });
                                }
                            }
                        }
                    }

                    await _context.SaveChangesAsync();
                    await LogKaydet(loginOlanPersonel, "Görev Güncellendi", $"Görev ID: {dbGorev.Id} güncellendi.", null);
                }

                return RedirectToAction("Gorev");
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
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

            // Length > 0 kontrolü eklendi
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

            if (fotoBytes != null)
                return File(fotoBytes, "image/jpeg");

            return NotFound();
        }

        // ⚡ N+1 DB OPTİMİZASYONU: Metoda isimler opsiyonel parametre olarak eklendi, DB hit'i önlendi.
        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, Gorev? gorev, string preHotelAdi = "", string preDeptAdi = "")
        {
            try
            {
                string departmanAdi = !string.IsNullOrEmpty(preDeptAdi) ? preDeptAdi : (personel?.DepartmanRefNavigation?.Adi ?? "");
                string hotelAdi = preHotelAdi;

                if (personel != null && personel.DepartmanRef != 0 && string.IsNullOrEmpty(hotelAdi))
                {
                    var data = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, hAdi = d.HotelRefNavigation.Adi })
                        .FirstOrDefaultAsync();

                    if (data != null)
                    {
                        hotelAdi = data.hAdi;
                        departmanAdi = data.Adi;
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Gorev",
                    KayitRefId = gorev?.Id ?? personel?.Id ?? 0,
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