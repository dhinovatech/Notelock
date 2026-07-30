using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Notelockv3.Services
{
    public class EncryptionService
    {
        private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("NTLK");
        private const byte Version2 = 0x02;

        private const int SaltSize = 16;
        private const int KeySize = 32; // 256 bit
        private const int IvSize = 16; // 128 bit (for Legacy V1)
        private const int GcmNonceSize = 12; // 96 bit (for V2 AES-GCM)
        private const int GcmTagSize = 16; // 128 bit (for V2 AES-GCM)
        private const int Iterations = 100000;

        /// <summary>
        /// Encrypts text using the new V2 format (AES-256-GCM + PBKDF2-SHA256) with NTLK magic header.
        /// </summary>
        public static byte[] Encrypt(string plainText, string password)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
            byte[] tag = new byte[GcmTagSize];
            byte[] cipherTextBytes = new byte[plainBytes.Length];
            byte[] key = Array.Empty<byte>();

            try
            {
                // Derive Key using PBKDF2-SHA256
                key = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    Iterations,
                    HashAlgorithmName.SHA256,
                    KeySize
                );

                using (var aesGcm = new AesGcm(key, GcmTagSize))
                {
                    aesGcm.Encrypt(nonce, plainBytes, cipherTextBytes, tag);
                }

                // Construct V2 Payload: [MAGIC 4B][VERSION 1B][SALT 16B][NONCE 12B][TAG 16B][CIPHERTEXT NB]
                using (var ms = new MemoryStream())
                {
                    ms.Write(MagicHeader, 0, MagicHeader.Length);
                    ms.WriteByte(Version2);
                    ms.Write(salt, 0, salt.Length);
                    ms.Write(nonce, 0, nonce.Length);
                    ms.Write(tag, 0, tag.Length);
                    ms.Write(cipherTextBytes, 0, cipherTextBytes.Length);

                    return ms.ToArray();
                }
            }
            finally
            {
                if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }

        /// <summary>
        /// Decrypts cipher data automatically handling V2 (AES-GCM with NTLK header) and legacy V1 (AES-CBC without header).
        /// </summary>
        public static string Decrypt(byte[] cipherData, string password)
        {
            if (cipherData == null || cipherData.Length < SaltSize + IvSize)
            {
                throw new InvalidDataException("Invalid or corrupted file format.");
            }

            // Check for NTLK header (4 bytes) + Version byte
            if (HasMagicHeader(cipherData))
            {
                byte version = cipherData[4];
                if (version == Version2)
                {
                    return DecryptV2(cipherData, password);
                }
                else
                {
                    throw new InvalidDataException($"Unsupported file format version ({version}).");
                }
            }

            // Fallback to Legacy V1 decryption for older encrypted files
            return DecryptLegacyV1(cipherData, password);
        }

        private static bool HasMagicHeader(byte[] data)
        {
            if (data.Length < 5) return false;
            return data[0] == (byte)'N' &&
                   data[1] == (byte)'T' &&
                   data[2] == (byte)'L' &&
                   data[3] == (byte)'K';
        }

        private static string DecryptV2(byte[] cipherData, string password)
        {
            const int HeaderSize = 5; // "NTLK" + version byte
            int minSize = HeaderSize + SaltSize + GcmNonceSize + GcmTagSize;

            if (cipherData.Length < minSize)
            {
                throw new InvalidDataException("Invalid or corrupted file format.");
            }

            byte[] salt = new byte[SaltSize];
            Array.Copy(cipherData, HeaderSize, salt, 0, SaltSize);

            byte[] nonce = new byte[GcmNonceSize];
            Array.Copy(cipherData, HeaderSize + SaltSize, nonce, 0, GcmNonceSize);

            byte[] tag = new byte[GcmTagSize];
            Array.Copy(cipherData, HeaderSize + SaltSize + GcmNonceSize, tag, 0, GcmTagSize);

            int cipherTextOffset = HeaderSize + SaltSize + GcmNonceSize + GcmTagSize;
            int cipherTextLength = cipherData.Length - cipherTextOffset;

            byte[] cipherTextBytes = new byte[cipherTextLength];
            Array.Copy(cipherData, cipherTextOffset, cipherTextBytes, 0, cipherTextLength);

            byte[] plainBytes = new byte[cipherTextLength];
            byte[] key = Array.Empty<byte>();

            try
            {
                key = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    Iterations,
                    HashAlgorithmName.SHA256,
                    KeySize
                );

                using (var aesGcm = new AesGcm(key, GcmTagSize))
                {
                    aesGcm.Decrypt(nonce, cipherTextBytes, tag, plainBytes);
                }

                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("Decryption failed. Incorrect password or corrupted file.");
            }
            finally
            {
                if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }

        private static string DecryptLegacyV1(byte[] cipherData, string password)
        {
            using (var ms = new MemoryStream(cipherData))
            {
                byte[] salt = new byte[SaltSize];
                if (ms.Read(salt, 0, salt.Length) != salt.Length)
                    throw new InvalidDataException("Invalid file format: unable to read salt.");

                byte[] iv = new byte[IvSize];
                if (ms.Read(iv, 0, iv.Length) != iv.Length)
                    throw new InvalidDataException("Invalid file format: unable to read IV.");

                byte[] key = Array.Empty<byte>();

                try
                {
                    key = Rfc2898DeriveBytes.Pbkdf2(
                        password,
                        salt,
                        Iterations,
                        HashAlgorithmName.SHA256,
                        KeySize
                    );

                    using (var aes = Aes.Create())
                    {
                        aes.KeySize = KeySize * 8;
                        aes.BlockSize = IvSize * 8;
                        aes.IV = iv;
                        aes.Key = key;

                        using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                        using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        using (var sr = new StreamReader(cs, Encoding.UTF8))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
                catch (CryptographicException)
                {
                    throw new InvalidDataException("Decryption failed. Incorrect password or corrupted file.");
                }
                finally
                {
                    if (key.Length > 0) CryptographicOperations.ZeroMemory(key);
                    CryptographicOperations.ZeroMemory(salt);
                }
            }
        }
    }
}
