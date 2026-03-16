using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json; // Newtonsoft yerine daha hızlı olan System.Text.Json eklendi
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
                await LogKaydet(loginOlanPersonel, "Görev Sayfasına Giriş Yapıldı", "Görev Listesi Görüntüleme", null);

                var hotelId = await _context.Departmen
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => d.HotelRef)
                    .FirstOrDefaultAsync();

                // PERFORMANS 1: AsSplitQuery eklendi. RAM'i şişiren kartezyen patlamasını önler.
                var hotel = await _context.Hotels
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(h => h.Departmen)
                        .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                            .ThenInclude(p => p.Gorevs)
                    .FirstOrDefaultAsync(h => h.Id == hotelId);

                if (hotel == null)
                    return RedirectToAction("Login", "Login");

                var gorevIds = hotel.Departmen.SelectMany(d => d.Personels).SelectMany(p => p.Gorevs).Select(g => g.Id).ToList();

                if (gorevIds.Any())
                {
                    var fotoIdListesi = await _context.GorevFotografs
                        .Where(gf => gorevIds.Contains(gf.GorevRef))
                        .Select(gf => new { gf.Id, gf.GorevRef })
                        .ToListAsync();

                    // PERFORMANS 2: ToLookup kullanılarak veriler bellekte indekslendi. Eşleşme O(1) hızına çıktı.
                    var fotoLookup = fotoIdListesi.ToLookup(f => f.GorevRef);

                    foreach (var departman in hotel.Departmen)
                    {
                        foreach (var personel in departman.Personels)
                        {
                            foreach (var gorev in personel.Gorevs)
                            {
                                gorev.GorevFotografs = fotoLookup[gorev.Id]
                                    .Select(f => new GorevFotograf { Id = f.Id, GorevRef = f.GorevRef })
                                    .ToList();
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

                _context.Gorevs.Add(model);
                await _context.SaveChangesAsync();

                if (Fotograf != null && Fotograf.Count > 0)
                {
                    foreach (var dosya in Fotograf)
                    {
                        if (dosya.Length > 0)
                        {
                            // PERFORMANS 3: Allocation overhead'i engellemek için Stream boyutu belirtildi.
                            using (var ms = new MemoryStream((int)dosya.Length))
                            {
                                await dosya.CopyToAsync(ms);
                                _context.GorevFotografs.Add(new GorevFotograf
                                {
                                    GorevRef = model.Id,
                                    Fotograf = ms.ToArray()
                                });
                            }
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                await LogKaydet(loginOlanPersonel, "Yeni Görev Eklendi", $"Görev ID: {model.Id} oluşturuldu.", null);
                // Dikkat: Yönlendirme Route yapınızla aynı olmalı
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

                var gorev = await _context.Gorevs.FindAsync(id);
                if (gorev != null)
                {
                    // EF Core 7.0+ ExecuteDeleteAsync kullanımı çok iyi, değiştirilmedi.
                    await _context.GorevFotografs
                        .Where(f => f.GorevRef == id)
                        .ExecuteDeleteAsync();

                    _context.Gorevs.Remove(gorev);
                    await _context.SaveChangesAsync();

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
                    dbGorev.Gorev1 = model.Gorev1;
                    dbGorev.PersonelNotu = model.PersonelNotu;

                    if (dbGorev.Durum != model.Durum)
                    {
                        if (model.Durum == 2 && dbGorev.CozumBaslamaTarihi == null)
                            dbGorev.CozumBaslamaTarihi = DateTime.Now;
                        else if (model.Durum == 3 && dbGorev.CozumBitisTarihi == null)
                            dbGorev.CozumBitisTarihi = DateTime.Now;
                    }

                    dbGorev.Durum = model.Durum;

                    // PERFORMANS 4: Update(dbGorev) kaldırıldı! FindAsync ile veri Track ediliyor. 
                    // SaveChangesAsync otomatik olarak sadece değişen kolonların sorgusunu oluşturur.

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

        // PERFORMANS 5: Tarayıcı 1 gün boyunca DB'yi yormayacak. Sadece byte[] çekilerek RAM ferahlatıldı.
        [HttpGet]
        [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> PersonelFotoGetir(long id)
        {
            var fotoBytes = await _context.PersonelFotografs
                .Where(f => f.PersonelRef == id)
                .Select(f => f.Fotograf)
                .FirstOrDefaultAsync();

            if (fotoBytes != null)
                return File(fotoBytes, "image/jpeg");

            return NotFound();
        }

        [HttpGet]
        [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> KanitFotoGetir(long id)
        {
            var fotoBytes = await _context.GorevFotografs
                .Where(f => f.Id == id)
                .Select(f => f.Fotograf)
                .FirstOrDefaultAsync();

            if (fotoBytes != null)
                return File(fotoBytes, "image/jpeg");

            return NotFound();
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, Gorev? gorev)
        {
            try
            {
                string departmanAdi = personel?.DepartmanRefNavigation?.Adi ?? "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    hotelAdi = await _context.Departmen
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => d.HotelRefNavigation.Adi)
                        .FirstOrDefaultAsync() ?? "";
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Gorev",
                    KayitRefId = gorev?.Id ?? personel?.Id ?? 0,
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