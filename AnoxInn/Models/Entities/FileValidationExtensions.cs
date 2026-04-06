using Microsoft.AspNetCore.Http;
using System.IO;
using System.Linq;

namespace AxonInn.Helpers
{
    public static class FileValidationExtensions
    {
        // İzin verilen uzantılar
        private static readonly string[] _permittedExtensions = { ".jpg", ".jpeg", ".png", ".gif" };

        public static bool IsValidImage(this IFormFile file)
        {
            if (file == null || file.Length == 0) return false;

            // 1. Uzantı Kontrolü
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !_permittedExtensions.Contains(ext))
            {
                return false;
            }

            // 2. Sihirli Numaralar (Magic Numbers) Kontrolü
            // Dosyanın ilk byte'larını okuyup gerçek formatını teyit ediyoruz.
            using var stream = file.OpenReadStream();
            var headerBytes = new byte[8]; // En uzun imza (PNG) için 8 byte yeterli

            // Eğer dosya 8 byte'tan küçükse zaten geçerli bir görsel olamaz
            if (stream.Read(headerBytes, 0, headerBytes.Length) < 8)
            {
                return false;
            }

            // Okuma işleminden sonra stream'i başa sarıyoruz ki 
            // kaydetme (SaveAs) aşamasında dosya bozuk veya eksik kaydedilmesin.
            stream.Position = 0;

            // Byte karşılaştırmaları
            if (ext == ".jpeg" || ext == ".jpg")
            {
                // JPEG imzası: FF D8 FF
                return headerBytes[0] == 0xFF &&
                       headerBytes[1] == 0xD8 &&
                       headerBytes[2] == 0xFF;
            }

            if (ext == ".png")
            {
                // PNG imzası: 89 50 4E 47 0D 0A 1A 0A
                return headerBytes[0] == 0x89 && headerBytes[1] == 0x50 &&
                       headerBytes[2] == 0x4E && headerBytes[3] == 0x47 &&
                       headerBytes[4] == 0x0D && headerBytes[5] == 0x0A &&
                       headerBytes[6] == 0x1A && headerBytes[7] == 0x0A;
            }

            if (ext == ".gif")
            {
                // GIF imzası: GIF87a veya GIF89a (47 49 46 38)
                return headerBytes[0] == 0x47 &&
                       headerBytes[1] == 0x49 &&
                       headerBytes[2] == 0x46 &&
                       headerBytes[3] == 0x38;
            }

            return false;
        }
    }
}