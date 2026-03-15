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

//    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
//#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
//        => optionsBuilder.UseSqlServer("Server=OnurLaptop;Database=axoninnc_db;User Id=sa;Password=12345+pl;TrustServerCertificate=True;");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Departman>(entity =>
        {
            entity.ToTable("Departman");

            entity.HasIndex(e => e.HotelRef, "IX_Departman_HotelRef");

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

            entity.HasIndex(e => e.PersonelRef, "IX_Gorev_PersonelRef");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CozumBaslamaTarihi).HasColumnType("datetime");
            entity.Property(e => e.CozumBitisTarihi).HasColumnType("datetime");
            entity.Property(e => e.Gorev1).HasColumnName("Gorev");
            entity.Property(e => e.KayitTarihi).HasColumnType("datetime");

            entity.HasOne(d => d.PersonelRefNavigation).WithMany(p => p.Gorevs)
                .HasForeignKey(d => d.PersonelRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Gorev_Personel");
        });

        modelBuilder.Entity<GorevFotograf>(entity =>
        {
            entity.ToTable("GorevFotograf");

            entity.Property(e => e.Id).HasColumnName("ID");

            entity.HasOne(d => d.GorevRefNavigation).WithMany(p => p.GorevFotografs)
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

            entity.HasIndex(e => e.DepartmanRef, "IX_Personel_DepartmanRef");

            entity.HasIndex(e => e.MailAdresi, "IX_Personel_MailAdresi");

            entity.HasIndex(e => e.TelefonNumarasi, "IX_Personel_TelefonNumarasi");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Adi).HasMaxLength(50);
            entity.Property(e => e.DogumTarihi).HasColumnType("datetime");
            entity.Property(e => e.MailAdresi).HasMaxLength(256);
            entity.Property(e => e.Soyadi).HasMaxLength(50);
            entity.Property(e => e.TelefonNumarasi).HasMaxLength(20);

            entity.HasOne(d => d.DepartmanRefNavigation).WithMany(p => p.Personels)
                .HasForeignKey(d => d.DepartmanRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Personel_Departman");
        });

        modelBuilder.Entity<PersonelFotograf>(entity =>
        {
            entity.ToTable("PersonelFotograf");

            entity.Property(e => e.Id).HasColumnName("ID");

            entity.HasOne(d => d.PersonelRefNavigation).WithMany(p => p.PersonelFotografs)
                .HasForeignKey(d => d.PersonelRef)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PersonelFotograf_Personel");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}