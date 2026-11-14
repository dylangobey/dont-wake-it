using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace DontWakeIt.Networking
{
    public enum RoomCodeCharset
    {
        LettersAndNumbers,
        LettersOnly,
        NumbersOnly
    }

    [Serializable]
    public class RoomCodeSettings
    {
        [Range(3, 12)] public int length = 6;

        public RoomCodeCharset charset = RoomCodeCharset.LettersAndNumbers;

        public bool avoidLookAlikes = true;
    }

    public static class RoomCode
    {
        public const int MinLength = 3;
        public const int MaxLength = 12;

        private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private const string Digits = "0123456789";
        private const string LookAlikes = "O0I1L";

        public static string Alphabet(RoomCodeSettings settings)
        {
            string source = settings.charset switch
            {
                RoomCodeCharset.LettersOnly => Letters,
                RoomCodeCharset.NumbersOnly => Digits,
                _ => Letters + Digits
            };

            if (!settings.avoidLookAlikes) return source;

            var sb = new StringBuilder(source.Length);
            foreach(char c in source)
                if(LookAlikes.IndexOf(c) < 0) sb.Append(c);
            return sb.ToString();
        }

        public static string Generate(RoomCodeSettings settings)
        {
            string alphabet = Alphabet(settings);
            int length = Mathf.Clamp(settings.length, MinLength, MaxLength);
            var chars = new char[length];
            for(int i = 0; i < length; i++)
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            return new string(chars);
        }

        public static string Normalize(string input)
        {
            if(string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach(char c in input.Trim())
            {
                if(c == ' ' || c == '-' || c == '_') continue;
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        public static bool IsPlausible(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < MinLength || code.Length > MaxLength) return false;
            foreach(char c in code)
                if(!char.IsLetterOrDigit(c) || c > 127) return false;
            return true;
        }
    }
}