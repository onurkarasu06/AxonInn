using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace AxonInn.Models;

public partial class AxonInnContext : DbContext
{
    public AxonInnContext()
    {
    }

    public AxonInnContext(DbContextOptions<AxonInnContext> options)
        : base(options)
    {
    }

    // Eklediğimiz AuditLog tablosu
    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<Departman> Departmen { get; set; }

    public virtual DbSet<Gorev> Gorevs { get; set; }

    public virtual DbSet<GorevFotograf> GorevFotografs { get; set; }

    public virtual DbSet<Hotel> Hotels { get; set; }

    public virtual DbSet<Personel> Personels { get; set; }

    public virtual DbSet<PersonelFotograf> PersonelFotografs { get; set; }

    public DbSet<Yorum> Yorum { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ⚡ KRİTİK HATA (NVARCHAR(MAX)) ÖNLEMİ: AuditLog tablosu kısıtlamaları eklendi
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLog", "axoninnc_user");
            entity.HasKey(e => e.Id);

            // Loglar sürekli tarihe göre sorgulanır, Table Scan'i (Tüm Tablo Tarama) önlemek için İndeks şarttır.
            entity.HasIndex(e => e.IslemTarihi, "IX_AuditLog_IslemTarihi");
            entity.HasIndex(e => e.KayitRefId, "IX_AuditLog_KayitRefId");
            entity.HasIndex(e => e.IlgiliTablo, "IX_AuditLog_IlgiliTablo");

            entity.Property(e => e.IslemTarihi).HasColumnType("datetime");

            // Uzunluk (MaxLength) sınırları getirilerek EF Core'un bu alanları "nvarchar(max)" olarak açması ve RAM'i felç etmesi engellendi.
            // İngilizce veya sayısal ifadeler içeren IlgiliTablo kolonuna IsUnicode(false) uygulandı.
            entity.Property(e => e.IlgiliTablo).HasMaxLength(50).IsUnicode(false);
            entity.Property(e => e.IslemTipi).HasMaxLength(150);
            entity.Property(e => e.YapanAdSoyad).HasMaxLength(150);
            entity.Property(e => e.YapanDepartmanAd).HasMaxLength(100);
            entity.Property(e => e.YapanHotelAd).HasMaxLength(100);
            entity.Property(e => e.IslemTarihi).HasDefaultValueSql("getdate()");
        });

        modelBuilder.Entity<Departman>(entity =>
        {
            entity.ToTable("Departman");

            entity.HasIndex(e => e.HotelRef, "IX_Departman_HotelRef_Covering")
       .IncludeProperties(e => e.Adi);

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Adi).HasMaxLength(50);

            entity.HasOne(d => d.HotelRefNavigation)
                .WithMany(p => p.Departmen)
                .HasForeignKey(d => d.HotelRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Departman_Hotel");
        });

        modelBuilder.Entity<Gorev>(entity =>
        {
            entity.ToTable("Gorev");

            // ⚡ DASHBOARD KOMBİNE İNDEKS: PersonelRef ve Durum ayrı ayrı değil, SQL'deki gibi tek bir kompozit indeks olarak tanımlandı.
            // Dashboard'daki COUNT ve GROUP BY sorgularının yükünü doğrudan bu indeks çeker.
            entity.HasIndex(e => new { e.PersonelRef, e.Durum }, "IX_Gorev_PersonelRef_Durum");

            // ⚡ FULL TABLE SCAN KORUMASI: Görev listesinde tarihe göre "OrderByDescending" yapıldığından bu indeks korundu.
            entity.HasIndex(e => e.KayitTarihi, "IX_Gorev_KayitTarihi").IsDescending();

            // ⚡ YAPAY ZEKA FİLTRELİ İNDEKS: Sadece AiKategori dolu olan kayıtları belleğe alan performanslı indeks eklendi.
            entity.HasIndex(e => e.AiKategori, "IX_Gorev_AiKategori")
                  .IncludeProperties(e => e.PersonelRef)
                  .HasFilter("([AiKategori] IS NOT NULL)");

            // --- KOLON YAPILANDIRMALARI ---
            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CozumBaslamaTarihi).HasColumnType("datetime");
            entity.Property(e => e.CozumBitisTarihi).HasColumnType("datetime");
            entity.Property(e => e.KayitTarihi).HasColumnType("datetime");

            // Açıklama ve Not alanlarının SQL'de NVarChar(Max) olup veritabanını şişirmemesi için kısıtlandı.
            entity.Property(e => e.Aciklama).HasColumnName("Aciklama").HasMaxLength(2000);
            entity.Property(e => e.PersonelNotu).HasMaxLength(2000);

            // Veritabanındaki AiKategori uzunluğu (50) ile eşitlendi (NVARCHAR(MAX) olmasını engeller).
            entity.Property(e => e.AiKategori).HasMaxLength(50);

            // --- İLİŞKİLER (FOREIGN KEYS) ---
            entity.HasOne(d => d.PersonelRefNavigation)
                .WithMany(p => p.Gorevs)
                .HasForeignKey(d => d.PersonelRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Gorev_Personel");
        });

        modelBuilder.Entity<GorevFotograf>(entity =>
        {
            entity.ToTable("GorevFotograf");

            entity.Property(e => e.Id).HasColumnName("ID");

            entity.HasOne(d => d.GorevRefNavigation)
                .WithMany(p => p.GorevFotografs)
                .HasForeignKey(d => d.GorevRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_GorevFotograf_Gorev");
        });

        modelBuilder.Entity<Hotel>(entity =>
        {
            entity.ToTable("Hotel");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Adi).HasMaxLength(50);
        });

        modelBuilder.Entity<Personel>(entity =>
        {
            entity.ToTable("Personel");

            // --- STANDART İNDEKSLER ---
            entity.HasIndex(e => e.DepartmanRef, "IX_Personel_DepartmanRef");
            entity.HasIndex(e => e.TelefonNumarasi, "IX_Personel_TelefonNumarasi");

            // ⚡ GÖREV DASHBOARD OPTİMİZASYONU: Beklemede/işlemde olan görevleri departmana göre sayarken tabloya (Key Lookup) gitmeyi engeller.
            entity.HasIndex(e => new { e.AktifMi, e.DepartmanRef }, "IX_Personel_Aktif_Departman")
                  .IncludeProperties(e => new { e.Adi, e.Soyadi });

            // ⚡ GİRİŞ (LOGIN) OPTİMİZASYONU: Login sorgusunun ihtiyaç duyduğu tüm kolonları kapsayan (Covering Index) devasa optimizasyon.
            entity.HasIndex(e => new { e.MailAdresi, e.AktifMi }, "IX_Personel_MailAdresi_Aktif_v2")
                  .IncludeProperties(e => new { e.DepartmanRef, e.Yetki, e.Sifre, e.Adi, e.Soyadi, e.MedenHali, e.TelefonNumarasi, e.MailOnayliMi });

            // ⚡ ŞİFRE SIFIRLAMA & MAİL ONAY: Tüm tabloyu taramak yerine SADECE token'ı dolu olanları bellekte tutan filtrelenmiş (Filtered) indeks.
            entity.HasIndex(e => new { e.VerificationToken, e.AktifMi }, "IX_Personel_VerificationToken_Aktif")
                  .IncludeProperties(e => new { e.MailAdresi, e.MailOnayliMi })
                  .HasFilter("[VerificationToken] IS NOT NULL");

            // --- KOLON YAPILANDIRMALARI ---
            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Adi).HasMaxLength(50);
            entity.Property(e => e.Soyadi).HasMaxLength(50);
            entity.Property(e => e.DogumTarihi).HasColumnType("datetime");

            // ⚡ VERİ TABANI ALAN TASARRUFU: Bu kolonlar özel Unicode karakter barındırmaz. 
            // IsUnicode(false) ile "VARCHAR" yapılarak Disk ve Bellek (RAM) boyutu yarı yarıya düşürüldü.
            entity.Property(e => e.MailAdresi).HasMaxLength(256).IsUnicode(false);
            entity.Property(e => e.TelefonNumarasi).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.Sifre).HasMaxLength(256).IsUnicode(false);

            entity.Property(e => e.MailOnayliMi).HasDefaultValue((byte)0);

            // GUID token formatı 36 karakterdir, 50 ile güvenli şekilde sınırlandırıldı.
            entity.Property(e => e.VerificationToken).HasMaxLength(50).IsUnicode(false);

            // --- İLİŞKİLER (FOREIGN KEYS) ---
            entity.HasOne(d => d.DepartmanRefNavigation)
                .WithMany(p => p.Personels)
                .HasForeignKey(d => d.DepartmanRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Personel_Departman");
        });

        modelBuilder.Entity<PersonelFotograf>(entity =>
        {
            entity.ToTable("PersonelFotograf");

            entity.Property(e => e.Id).HasColumnName("ID");

            entity.HasOne(d => d.PersonelRefNavigation)
                .WithMany(p => p.PersonelFotografs)
                .HasForeignKey(d => d.PersonelRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PersonelFotograf_Personel");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}