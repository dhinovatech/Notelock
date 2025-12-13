using System.Security.Cryptography;
using System.Text;

namespace Notelock.Services
{
    public class EncryptionService
    {
        private const int SaltSize = 16;
        private const int KeySize = 32; // 256 bit
        private const int IvSize = 16; // 128 bit
        private const int Iterations = 100000;

        public static byte[] Encrypt(string plainText, string password)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = KeySize * 8;
                aes.BlockSize = IvSize * 8;

                // Generate Salt
                byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);

                // Derive Key and IV using Pbkdf2
                byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    Iterations,
                    HashAlgorithmName.SHA256,
                    KeySize
                );

                aes.GenerateIV(); // Generate a random IV

                aes.Key = key;

                using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                using (var ms = new MemoryStream())
                {
                    // Write Salt and IV to the beginning of the stream
                    ms.Write(salt, 0, salt.Length);
                    ms.Write(aes.IV, 0, aes.IV.Length);

                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs))
                    {
                        sw.Write(plainText);
                    }
                    return ms.ToArray();
                }
            }
        }

        public static string Decrypt(byte[] cipherData, string password)
        {
            using (var ms = new MemoryStream(cipherData))
            {
                // Read Salt
                byte[] salt = new byte[SaltSize];
                if (ms.Read(salt, 0, salt.Length) != salt.Length)
                    throw new Exception("Invalid file format");

                // Read IV
                byte[] iv = new byte[IvSize];
                if (ms.Read(iv, 0, iv.Length) != iv.Length)
                    throw new Exception("Invalid file format");

                using (var aes = Aes.Create())
                {
                    aes.KeySize = KeySize * 8;
                    aes.BlockSize = IvSize * 8;
                    aes.IV = iv;

                    // Derive Key using Pbkdf2
                    byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                        password,
                        salt,
                        Iterations,
                        HashAlgorithmName.SHA256,
                        KeySize
                    );
                    aes.Key = key;

                    using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                    using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
        }
    }
}
