using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AxonInn.Models
{
    // Veritabanındaki şema (axoninnc_user) ve tablo adını belirtiyoruz
    [Table("AuditLog", Schema = "axoninnc_user")]
    public class AuditLog
    {
        [Key]
        public long Id { get; set; }

        public DateTime? IslemTarihi { get; set; }

        [MaxLength(50)]
        public string? IlgiliTablo { get; set; }

        public long? KayitRefId { get; set; }
        [MaxLength(250)]
        public string? IslemTipi { get; set; }
        public string? EskiDeger { get; set; }
        public string? YeniDeger { get; set; }

        [MaxLength(100)]
        public string? YapanHotelAd { get; set; }

        [MaxLength(100)]
        public string? YapanDepartmanAd { get; set; }

        [MaxLength(150)]
        public string? YapanAdSoyad { get; set; }
    }
}