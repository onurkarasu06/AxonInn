using System.ComponentModel.DataAnnotations;

namespace AxonInn.Models.Entities
{
    public class Iletisim
    {
        [Required]
        [MaxLength(100)]
        public string AdSoyad { get; set; }

        [Required]
        [EmailAddress]
        [MaxLength(150)]
        public string Email { get; set; }

        [Required]
        [MaxLength(200)]
        public string Konu { get; set; }

        [Required]
        [MaxLength(2000)]
        public string Mesaj { get; set; }
    }
}
