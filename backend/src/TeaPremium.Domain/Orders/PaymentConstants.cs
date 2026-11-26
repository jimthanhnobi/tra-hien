using System.Text.RegularExpressions;

namespace TeaPremium.Domain.Orders;

public static class PaymentMethod
{
    public const string BankTransfer = "bank_transfer";
    public const string Cod = "cod";
    public const string Cash = "cash";

    public static bool IsValid(string? value) => value is BankTransfer or Cod or Cash;

    public static bool IsManualRecordable(string? value) => value is BankTransfer or Cash;
}

public static class PaymentProvider
{
    public const string Manual = "manual";
    public const string Sepay = "sepay";

    public static bool IsValid(string? value) => value is Manual or Sepay;
}

public static class PaymentRecordStatus
{
    public const string AwaitingSettlement = "awaiting_settlement";
    public const string Confirmed = "confirmed";
    public const string Voided = "voided";

    public static bool IsValid(string? value) => value is AwaitingSettlement or Confirmed or Voided;
}

public static class RefundMethod
{
    public const string BankTransfer = "bank_transfer";
    public const string Cash = "cash";

    public static bool IsValid(string? value) => value is BankTransfer or Cash;
}

public static class RefundKind
{
    public const string Overpayment = "overpayment";
    public const string Return = "return";
    public const string Cancellation = "cancellation";
    public const string Adjustment = "adjustment";

    public static bool IsValid(string? value) => value is Overpayment or Return or Cancellation or Adjustment;

    public static bool AllowsShippingRefund(string? value) => value is Return or Adjustment;
}

public static class AwaitingPaymentType
{
    public const string BankTransfer = "bank_transfer";
    public const string Cod = "cod";

    public static bool IsValid(string? value) => value is BankTransfer or Cod;
}

public static class ReturnRequestStatus
{
    public const string Requested = "requested";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Resolved = "resolved";

    public static bool IsValid(string? value) => value is Requested or Approved or Rejected or Resolved;
}

public static class ReturnResolution
{
    public const string Refund = "refund";
    public const string Replacement = "replacement";

    public static bool IsValid(string? value) => value is Refund or Replacement;
}

public static class ReturnReason
{
    public const string DamagedInTransit = "damaged_in_transit";
    public const string Defective = "defective";
    public const string WrongItem = "wrong_item";

    public static bool IsValid(string? value) => value is DamagedInTransit or Defective or WrongItem;
}

public static partial class ReturnCode
{
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && ReturnCodePattern().IsMatch(value);

    [GeneratedRegex("^DT-[0-9]{6}-[0-9]{3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReturnCodePattern();
}

public static class OrderConfirmReasonCodes
{
    public const string AutoConfirmedOnFullPayment = "auto_confirmed_on_full_payment";
}
