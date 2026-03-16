using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace AxonInn.Controllers
{
    public class DepartmanController : Controller
    {
        private readonly AxonInnContext _context;

        public DepartmanController(AxonInnContext context)
        {
            _context = context;
        }

        [Route("Departmanlar")]
        [HttpGet]
        public async Task<IActionResult> Departman()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);
                await LogKaydet(loginOlanPersonel, "Departman Sayfasına Giriş Yapıldı", "Sayfa Görüntüleme", null);

                // İYİLEŞTİRME: İki ayrı veritabanı sorgusu tek bir sorguya indirgendi. 
                // Hotel tablosuna giderken, doğrudan kullanıcının DepartmanRef'i üzerinden filtreleme yapıldı.
                var hotel = await _context.Hotels
                    .AsNoTracking()
                    .Include(h => h.Departmen)
                        .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                    .FirstOrDefaultAsync(h => h.Departmen.Any(d => d.Id == loginOlanPersonel.DepartmanRef));

                if (hotel == null)
                {
                    return RedirectToAction("Login", "Login");
                }

                ViewData["Title"] = "AxonInn";
                return View("Departman", hotel);
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelEkle(Personel yeniPersonel, IFormFile yuklenenFoto)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                bool kullaniciVarmi = await _context.Personels.AnyAsync(p => p.TelefonNumarasi == yeniPersonel.TelefonNumarasi || p.MailAdresi == yeniPersonel.MailAdresi);

                if (kullaniciVarmi)
                {
                    await LogKaydet(loginOlanPersonel, "Personel Ekleme Hatası", "Mail adresi veya telefon numarası eşleştiği için kayıt yapılamadı.", yeniPersonel);
                    TempData["Mesaj"] = "Mail adresi veya telefon numarası ile eşleşen bir personel kayıtlı olduğu için kaydet işlemi yapılamaz.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction("Departman", "Departman");
                }

                yeniPersonel.AktifMi = 1;
                _context.Personels.Add(yeniPersonel);

                // DÜZELTME 1: Önce personeli kaydediyoruz ki veritabanı yeniPersonel.Id değerini oluştursun.
                await _context.SaveChangesAsync();

                if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                {
                    using var ms = new MemoryStream(); // Modern Using (süslü parantez kalabalığını azaltır)
                    await yuklenenFoto.CopyToAsync(ms);

                    var foto = new PersonelFotograf
                    {
                        // DÜZELTME 2: Hata veren "Personel = yeniPersonel" satırını kaldırıp, ID atamasını yapıyoruz.
                        PersonelRef = yeniPersonel.Id,
                        Fotograf = ms.ToArray()
                    };
                    _context.PersonelFotografs.Add(foto);

                    // DÜZELTME 3: Fotoğrafı da veritabanına işliyoruz.
                    await _context.SaveChangesAsync();
                }

                await LogKaydet(loginOlanPersonel, "Yeni Personel Eklendi", "Personel Başarıyla Kaydedildi", yeniPersonel);

                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelSil(long id)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                bool gorevVarMi = await _context.Gorevs.AnyAsync(g => g.PersonelRef == id);

                if (gorevVarMi)
                {
                    await LogKaydet(loginOlanPersonel, "Personel Silme Hatası", "Personele kayıtlı görev bulunduğu için silinemez.", null);
                    TempData["Mesaj"] = "Personele kayıtlı görev bulunduğu için silinemez.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction("Departman", "Departman");
                }

                // İYİLEŞTİRME: Ana nesneyi bellekten çağırıp (FindAsync) sonra silmek (Remove) gereksiz bir işlemdir. 
                // ExecuteDeleteAsync ile doğrudan SQL üzerinde siliyoruz.
                await _context.PersonelFotografs.Where(f => f.PersonelRef == id).ExecuteDeleteAsync();
                await _context.Personels.Where(p => p.Id == id).ExecuteDeleteAsync();

                await LogKaydet(loginOlanPersonel, "Personel Silindi", $"Personel ID: {id} başarıyla silindi.", null);

                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception ex)
            {
                string gercekHata = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = gercekHata });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelGuncelle(Personel p, IFormFile yuklenenFoto)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");
                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                var dbPersonel = await _context.Personels.FindAsync(p.Id);
                if (dbPersonel != null)
                {
                    dbPersonel.Adi = p.Adi;
                    dbPersonel.Soyadi = p.Soyadi;
                    dbPersonel.DepartmanRef = p.DepartmanRef;
                    dbPersonel.TelefonNumarasi = p.TelefonNumarasi;
                    dbPersonel.MailAdresi = p.MailAdresi;
                    dbPersonel.MedenHali = p.MedenHali;
                    dbPersonel.Yetki = p.Yetki;

                    if (!string.IsNullOrEmpty(p.Sifre))
                    {
                        dbPersonel.Sifre = p.Sifre;
                    }

                    // İYİLEŞTİRME: _context.Personels.Update(dbPersonel); SATIRI KALDIRILDI.
                    // EF Core zaten dbPersonel'i takip (track) ettiği için, sadece değişen property'leri bulur ve veritabanına o kısımlar için Update atar.
                    // Update() metodunu çağırmak ise nesnenin TÜM alanlarını değişmiş gibi işaretler ve gereksiz SQL yükü oluşturur.

                    if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                    {
                        var mevcutFoto = await _context.PersonelFotografs.FirstOrDefaultAsync(f => f.PersonelRef == p.Id);

                        using var ms = new MemoryStream();
                        await yuklenenFoto.CopyToAsync(ms);
                        if (mevcutFoto != null)
                        {
                            mevcutFoto.Fotograf = ms.ToArray();
                            // Burada da Update'e gerek yok, referans takipte.
                        }
                        else
                        {
                            _context.PersonelFotografs.Add(new PersonelFotograf { PersonelRef = p.Id, Fotograf = ms.ToArray() });
                        }
                    }

                    await _context.SaveChangesAsync();
                    await LogKaydet(loginOlanPersonel, "Personel Güncellendi", $"Personel ID: {p.Id} başarıyla güncellendi.", dbPersonel);
                }

                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, Personel? islemGorenPersonel)
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
                    IlgiliTablo = "Personel",
                    KayitRefId = islemGorenPersonel?.Id ?? personel?.Id ?? 0,
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