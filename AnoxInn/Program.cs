using AxonInn.Models.Analitik;
using AxonInn.Models.Context;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AxonInnContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));



// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddHttpClient<GeminiApiService>();


// --- 1. SESSION (OTURUM) SERVİSLERİ EKLENİYOR ---
builder.Services.AddDistributedMemoryCache(); // Oturum verilerini bellekte tutmak için gerekli
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60); // Kullanıcı 60 dakika işlem yapmazsa oturum düşer
    options.Cookie.HttpOnly = true;                 // Güvenlik: Çerezlere client-side scriptlerden erişilemez
    options.Cookie.IsEssential = true;              // GDPR/KVKK uyumluluğu için çerezi zorunlu kılar
});
// ------------------------------------------------

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseRouting();

// --- 2. SESSION MIDDLEWARE AKTİFLEŞTİRİLİYOR ---
// Dikkat: Mutlaka UseRouting ve UseAuthorization arasında olmalıdır!
app.UseSession();
// -----------------------------------------------

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Login}/{action=Login}/{id?}")
    .WithStaticAssets();

app.Run();