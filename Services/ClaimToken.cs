using System.Security.Cryptography;
using System.Text;

namespace ExamArchive.Services;

// The receipt code an anonymous submitter is given so they can come back and find
// out what happened to their paper. It is a bearer credential: whoever holds it can
// read that paper's status, so it must be unguessable rather than merely unique.
public static class ClaimToken
{
    // Crockford's base32: digits and uppercase letters, minus I, L, O and U. Chosen
    // because a person may write this down and type it back a week later.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // Twenty symbols from a 32-letter alphabet is 100 bits, which is why the lookup
    // needs no rate limit for correctness.
    private const int Length = 20;

    private const int GroupSize = 5;

    // Produces a fresh code in its display form, e.g. 4K7M2-QRT9X-BD3HF-WY6NP.
    // Reducing each byte modulo 32 is unbiased only because 256 divides evenly by 32.
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(Length);
        var token = new StringBuilder(Length + (Length / GroupSize) - 1);

        for (var i = 0; i < Length; i++)
        {
            if (i > 0 && i % GroupSize == 0)
            {
                token.Append('-');
            }

            token.Append(Alphabet[bytes[i] % Alphabet.Length]);
        }

        return token.ToString();
    }

    // Forgives everything a human plausibly gets wrong while copying: lowercase,
    // missing or extra dashes, spaces, and the confusable letters the alphabet
    // excludes. Excluding O only helps if typing O still finds the code.
    public static string Normalize(string token)
    {
        var normalized = new StringBuilder(token.Length);

        foreach (var c in token)
        {
            var upper = char.ToUpperInvariant(c);

            if (upper is 'I' or 'L')
            {
                normalized.Append('1');
            }
            else if (upper is 'O')
            {
                normalized.Append('0');
            }
            else if (Alphabet.Contains(upper))
            {
                normalized.Append(upper);
            }
        }

        return normalized.ToString();
    }

    // SHA-256 of the normalized code, as hex. A plain hash rather than a slow
    // password hash: the secret is 100 random bits, so there is no dictionary to
    // defend against.
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(token))));
}
