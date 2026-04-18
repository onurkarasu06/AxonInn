using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AxonInn.Services
{
    public static class WhatsAppSender
    {
        // 1. ADIM: Green API panelinden aldığın Instance ID ve Token bilgilerini buraya giriyorsun.
        private static readonly string InstanceId = "SENIN_INSTANCE_ID_BURAYA";
        private static readonly string ApiTokenInstance = "SENIN_TOKEN_BURAYA";

        // Green API URL yapısı
        private static readonly string ApiUrl = $"https://api.green-api.com/waInstance{InstanceId}/sendMessage/{ApiTokenInstance}";

        /// <summary>
        /// Belirtilen telefon numarasına WhatsApp üzerinden mesaj gönderir.
        /// Kullanım: await WhatsAppSender.SendWhatsAppMessageAsync("905327780022", "Yeni görev eklendi!");
        /// </summary>
        public static async Task<bool> SendWhatsAppMessageAsync(string phoneNumber, string message)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber) || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            // Green API numaraların sonuna @c.us eklenmesini ister.
            // Gelen numaranın başında + veya 0 varsa temizlemek iyi bir pratiktir, doğrudan ülke koduyla başlamalı.
            string cleanNumber = Regex.Replace(phoneNumber, @"[^\d]", "");

            // Eğer numara 0 ile başlıyorsa 9 ekleyerek 90... formatına getirir
            if (cleanNumber.StartsWith("0")) cleanNumber = "9" + cleanNumber;

            string chatId = $"{cleanNumber}@c.us";

            // Gönderilecek JSON verisi
            var payload = new
            {
                chatId = chatId,
                message = message
            };

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    StringContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // POST isteği atıyoruz
                    HttpResponseMessage response = await client.PostAsync(ApiUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }

                    // Başarısız olursa API'nin döndüğü hatayı okuyabiliriz
                    string errorResult = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"WhatsApp Gönderim Başarısız: {errorResult}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WhatsApp Hata İstisnası: {ex.Message}");
                return false;
            }
        }
    }
}