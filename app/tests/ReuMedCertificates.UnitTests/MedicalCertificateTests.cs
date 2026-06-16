using FluentAssertions;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Domain.Enums;
using Xunit;

namespace ReuMedCertificates.UnitTests;

public sealed class MedicalCertificateTests
{
    [Fact]
    public void GetStatus_ShouldReturnActive_WhenCertificateIsStillValid()
    {
        var certificate = new MedicalCertificate
        {
            StartDate = new DateOnly(2026, 5, 1),
            EndDate = new DateOnly(2026, 5, 20)
        };

        var result = certificate.GetStatus(new DateOnly(2026, 5, 10));

        result.Should().Be(CertificateStatus.Active);
    }

    [Fact]
    public void GetStatus_ShouldReturnEndsToday_WhenCertificateEndsToday()
    {
        var certificate = new MedicalCertificate
        {
            StartDate = new DateOnly(2026, 4, 1),
            EndDate = new DateOnly(2026, 4, 30)
        };

        var result = certificate.GetStatus(new DateOnly(2026, 4, 30));

        result.Should().Be(CertificateStatus.EndsToday);
    }

    [Fact]
    public void GetStatus_ShouldReturnExpired_WhenCertificateDateHasPassed()
    {
        var certificate = new MedicalCertificate
        {
            StartDate = new DateOnly(2026, 4, 1),
            EndDate = new DateOnly(2026, 4, 30)
        };

        var result = certificate.GetStatus(new DateOnly(2026, 5, 1));

        result.Should().Be(CertificateStatus.Expired);
    }
}
