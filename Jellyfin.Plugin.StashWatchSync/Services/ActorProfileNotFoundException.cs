using System;

namespace JFToStashSync.Services;

/// <summary>
/// A verified exact-name search returned no profile, or a known actor profile URL
/// returned HTTP 404. Only these definitive outcomes receive the 30-day negative TTL.
/// Network errors, ambiguous matches, access challenges and changed page markup do not.
/// </summary>
internal sealed class ActorProfileNotFoundException : InvalidOperationException
{
    public ActorProfileNotFoundException(string message) : base(message) { }
}
