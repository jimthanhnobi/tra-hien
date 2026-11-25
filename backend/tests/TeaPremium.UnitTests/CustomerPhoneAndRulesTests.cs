using System.Text.RegularExpressions;
using TeaPremium.Domain.Crm;

namespace TeaPremium.UnitTests;

public sealed class CustomerPhoneAndRulesTests
{
    [Theory]
    [InlineData("0901234567", "+84901234567")]
    [InlineData("032.123.4567", "+84321234567")]
    [InlineData("056-123-4567", "+84561234567")]
    [InlineData("(070) 123 4567", "+84701234567")]
    [InlineData("84811234567", "+84811234567")]
    [InlineData("+84981234567", "+84981234567")]
    [InlineData("  \u00A0+84 (901)-234.567\t ", "+84901234567")]
    [InlineData(null, null)]
    public void BrCrm01NormalizePhoneAcceptsValidVietnameseMobileFormatsAndSeparators(string? raw, string? expected)
    {
        var ok = CustomerRules.TryNormalizePhone(raw, out var normalized);
        Assert.True(ok);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("02438223344")]
    [InlineData("02838223344")]
    [InlineData("0123456789")]
    [InlineData("0412345678")]
    [InlineData("0612345678")]
    [InlineData("+84000000000")]
    [InlineData("+840901234567")]
    [InlineData("090123456")]
    [InlineData("09012345678")]
    [InlineData("\u0660\u0669\u0660\u0661\u0662\u0663\u0664\u0665\u0666\u0667")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0901234567\0")]
    [InlineData("090123456a")]
    public void BrCrm01NormalizePhoneRejectsLandlineInvalidPrefixWrongLengthUnicodeDigitsAndSentinel(string raw)
    {
        var ok = CustomerRules.TryNormalizePhone(raw, out var normalized);
        Assert.False(ok);
        Assert.Null(normalized);
    }

    [Fact]
    public void BrCrm11AnonymizedOrderPhoneConstantRemainsValidForDbRegexWithoutWeakeningInputNormalization()
    {
        Assert.Equal("+84000000000", CustomerRules.AnonymizedOrderPhone);
        Assert.Matches(new Regex(@"^\+84[0-9]{9}$", RegexOptions.CultureInvariant), CustomerRules.AnonymizedOrderPhone);
        Assert.False(CustomerRules.TryNormalizePhone(CustomerRules.AnonymizedOrderPhone, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void BrCrm14BuildSearchSourceIncludesFullNameZaloCodeLocalZeroPhoneAndEmail()
    {
        var source = CustomerRules.BuildSearchSource(
            "  Đặng Trần Hiên ",
            " Trà Cụ Tử Sa ",
            "KH001000",
            "+84901234567",
            "hien@example.test");
        Assert.Equal("Đặng Trần Hiên Trà Cụ Tử Sa KH001000 0901234567 hien@example.test", source);

        var withoutOptionals = CustomerRules.BuildSearchSource("Nguyễn An", null, "KH001001", null, "  ");
        Assert.Equal("Nguyễn An KH001001", withoutOptionals);
    }

    [Theory]
    [InlineData("0901 234 567", "901234567")]
    [InlineData("0901 234", "901234")]
    [InlineData("+84 901-234", "901234")]
    [InlineData("+84901-234-567", "901234567")]
    [InlineData("84901234", "901234")]
    [InlineData("4567", "4567")]
    [InlineData("0567", "0567")]
    [InlineData("12", null)]
    [InlineData("KH001000", null)]
    [InlineData("dang tran", null)]
    [InlineData("  ", null)]
    public void CustomerSearchExtractPhoneSearchDigitsHandlesLocalE164AndTailFragments(string query, string? expected)
    {
        Assert.Equal(expected, CustomerRules.ExtractPhoneSearchDigits(query));
    }

    [Theory]
    [InlineData(1L, "KH000001")]
    [InlineData(1000L, "KH001000")]
    [InlineData(999999L, "KH999999")]
    [InlineData(1000000L, "KH1000000")]
    public void CustomerCodeFormatsSixDigitMinimumAndDoesNotTruncateAboveMillion(long sequence, string expected)
    {
        var code = CustomerRules.FormatCustomerCode(sequence);
        Assert.Equal(expected, code);
        Assert.True(CustomerRules.IsValidCustomerCode(code));
    }

    [Theory]
    [InlineData("+84901234567", null, "active", false, true)]
    [InlineData(null, "Tra Hien", "active", false, true)]
    [InlineData(null, null, "active", false, false)]
    [InlineData(null, "   ", "do_not_contact", false, false)]
    [InlineData(null, null, "erased", false, true)]
    [InlineData(null, null, "active", true, true)]
    public void CustomerContactInvariantEnforcesPhoneOrZaloUnlessErasedOrMerged(
        string? phone, string? zalo, string status, bool merged, bool expected)
    {
        Guid? mergedInto = merged ? Guid.CreateVersion7() : null;
        Assert.Equal(expected, CustomerRules.HasValidContact(phone, zalo, status, mergedInto));

        var id = Guid.CreateVersion7();
        Assert.True(CustomerRules.IsSelfReferral(id, id));
        Assert.False(CustomerRules.IsSelfReferral(id, Guid.CreateVersion7()));
        Assert.False(CustomerRules.IsSelfReferral(id, null));
    }
}
