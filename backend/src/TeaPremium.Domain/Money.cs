namespace TeaPremium.Domain;

// DEC-20: VND is an integer. Negative values require an explicit adjustment path.
public readonly record struct Money(long Vnd)
{
    public long Vnd { get; } = Vnd < 0
        ? throw new DomainException("negative_money", "Số tiền không được âm.")
        : Vnd;

    private Money(long vnd, bool allowNegative) : this(0)
    {
        if (!allowNegative && vnd < 0)
        {
            throw new DomainException("negative_money", "Số tiền không được âm.");
        }

        Vnd = vnd;
    }

    public static Money Adjustment(long vnd) => new(vnd, allowNegative: true);

    public static Money operator +(Money left, Money right) => new(checked(left.Vnd + right.Vnd));

    public static Money operator -(Money left, Money right) => new(checked(left.Vnd - right.Vnd));
}
