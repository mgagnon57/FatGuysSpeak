using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Helpers;

public sealed class FakeGoogleTokenValidator : IGoogleTokenValidator
{
    public GoogleIdentity? Identity { get; set; }
    public Exception? ThrowOnValidate { get; set; }

    public Task<GoogleIdentity> ValidateAsync(string idToken)
    {
        if (ThrowOnValidate is not null) throw ThrowOnValidate;
        return Task.FromResult(Identity
            ?? throw new InvalidOperationException("FakeGoogleTokenValidator.Identity must be set before use."));
    }
}
