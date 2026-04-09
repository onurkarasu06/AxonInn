using AxonInn.Models.Entities;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonInn.Services
{
    public interface ICurrentUserService
    {
        Personel? GetUser();
    }

    public class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true
        };

        public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public Personel? GetUser()
        {
            try
            {
                var session = _httpContextAccessor.HttpContext?.Session;
                if (session == null) return null;

                var personelJson = session.GetString("GirisYapanPersonel");
                return string.IsNullOrEmpty(personelJson) ? null : JsonSerializer.Deserialize<Personel>(personelJson, _jsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }
}