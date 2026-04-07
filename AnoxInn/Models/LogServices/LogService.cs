using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AxonInn.Services
{
    public class LogService : ILogService
    {
        private readonly AxonInnContext _context;

        public LogService(AxonInnContext context)
        {
            _context = context;
        }

        public async Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string eskiDeger, string yeniDeger, string oncedenAlinanHotelAd = "", string oncedenAlinanDepartmanAd = "")
        {
            try
            {
                if (personel == null) return false;

                string departmanAdi = oncedenAlinanDepartmanAd;
                string hotelAdi = oncedenAlinanHotelAd;

                if (personel.DepartmanRef != 0 && string.IsNullOrWhiteSpace(hotelAdi))
                {
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : string.Empty })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
                        departmanAdi = depBilgisi.Adi ?? string.Empty;
                        hotelAdi = depBilgisi.HotelAdi ?? string.Empty;
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "SayfaZiyareti",
                    KayitRefId = personel.Id,
                    IslemTipi = islemTipi,
                    EskiDeger = eskiDeger,
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
                // Gerçek bir senaryoda buradaki hatayı da ILogger ile sisteme yazdırmak faydalı olabilir.
                return false;
            }
        }
    }
}