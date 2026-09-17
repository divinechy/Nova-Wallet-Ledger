namespace NovaWalletLedger.Api.Exceptions;

/// <summary>Base type for domain errors that should be translated into RFC 7807 responses.</summary>
public abstract class DomainException : Exception
{
    public abstract int StatusCode { get; }
    public abstract string Title { get; }

    protected DomainException(string message) : base(message) { }
}

public class WalletNotFoundException : DomainException
{
    public override int StatusCode => 404;
    public override string Title => "Wallet not found";
    public WalletNotFoundException(Guid walletId) : base($"Wallet '{walletId}' was not found.") { }
}

public class InsufficientFundsException : DomainException
{
    public override int StatusCode => 422;
    public override string Title => "Insufficient funds";
    public InsufficientFundsException(Guid walletId) : base($"Wallet '{walletId}' does not have sufficient funds for this transfer.") { }
}

public class DailyLimitExceededException : DomainException
{
    public override int StatusCode => 422;
    public override string Title => "Daily outbound limit exceeded";
    public DailyLimitExceededException(long limitKobo) : base($"This transfer would exceed the wallet's daily outbound limit of {limitKobo} kobo.") { }
}

public class InvalidAmountException : DomainException
{
    public override int StatusCode => 400;
    public override string Title => "Invalid amount";
    public InvalidAmountException(string message) : base(message) { }
}

public class SameWalletTransferException : DomainException
{
    public override int StatusCode => 400;
    public override string Title => "Invalid transfer";
    public SameWalletTransferException() : base("Source and destination wallet must be different.") { }
}

public class IdempotencyKeyMissingException : DomainException
{
    public override int StatusCode => 400;
    public override string Title => "Idempotency-Key header is required";
    public IdempotencyKeyMissingException() : base("The Idempotency-Key header is required for this endpoint.") { }
}

/// <summary>Same key, different payload — must be rejected per spec.</summary>
public class IdempotencyKeyConflictException : DomainException
{
    public override int StatusCode => 409;
    public override string Title => "Idempotency-Key reused with a different payload";
    public IdempotencyKeyConflictException(string key) : base($"Idempotency key '{key}' was already used with a different request payload.") { }
}

/// <summary>Another request with the same key is still being processed.</summary>
public class IdempotencyRequestInFlightException : DomainException
{
    public override int StatusCode => 409;
    public override string Title => "Duplicate request in flight";
    public IdempotencyRequestInFlightException(string key) : base($"A request with idempotency key '{key}' is already being processed.") { }
}
