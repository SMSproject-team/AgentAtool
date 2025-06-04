using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

namespace ATool
{
    class Program
    {
        // encreator와 동일한 키, IV 사용 필수
        static readonly byte[] aesKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"); // 32 bytes
        static readonly byte[] aesIV = Encoding.UTF8.GetBytes("abcdef9876543210"); // 16 bytes

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("사용법: atool.exe <암호화된파일경로>");
                return;
            }

            string encryptedFilePath = args[0];

            if (!File.Exists(encryptedFilePath))
            {
                Log($"파일이 존재하지 않습니다: {encryptedFilePath}");
                return;
            }

            if (!encryptedFilePath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                Log("'.enc' 확장자만 처리할 수 있습니다.");
                return;
            }

            ProcessEncryptedFile(encryptedFilePath);
        }

        static void ProcessEncryptedFile(string encryptedFilePath)
        {
            string directory = Path.GetDirectoryName(encryptedFilePath);
            string fileName = Path.GetFileName(encryptedFilePath);
            string originalFileName = fileName.Substring(0, fileName.Length - 4); // .enc 제거
            string originalFilePath = Path.Combine(directory, originalFileName);
            string backupFilePath = Path.Combine(directory, "~" + fileName + ".bak");

            if (File.Exists(originalFilePath) || File.Exists(backupFilePath))
            {
                Log($"파일 충돌 감지: {originalFileName} 또는 {backupFilePath}");
                return;
            }

            try
            {
                File.Move(encryptedFilePath, backupFilePath);
                DecryptFile(backupFilePath, originalFilePath);
                File.SetAttributes(originalFilePath, FileAttributes.Normal); // 읽기 전용 방지

                Process process = StartAssociatedProgram(originalFilePath);

                if (process != null)
                {
                    Task.Run(() => MonitorAndReEncrypt(process, originalFilePath, backupFilePath, encryptedFilePath));
                }
                else
                {
                    CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                    Log("연결된 프로그램을 실행할 수 없습니다.");
                }
            }
            catch (Exception ex)
            {
                Log($"처리 오류: {ex.Message}\n{ex.StackTrace}");

                if (File.Exists(backupFilePath) && !File.Exists(encryptedFilePath))
                {
                    File.Move(backupFilePath, encryptedFilePath);
                }
            }
        }

        static void DecryptFile(string encryptedFilePath, string decryptedFilePath)
        {
            string extension = Path.GetExtension(decryptedFilePath).ToLower();

            using (FileStream input = new FileStream(encryptedFilePath, FileMode.Open, FileAccess.Read))
            using (FileStream output = new FileStream(decryptedFilePath, FileMode.Create, FileAccess.Write))
            {
                try
                {
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = aesKey;
                        aes.IV = aesIV;

                        using (CryptoStream cryptoStream = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        {
                            if (extension == ".txt")
                            {
                                using (var reader = new StreamReader(cryptoStream, Encoding.UTF8))
                                using (var writer = new StreamWriter(output, Encoding.UTF8))
                                {
                                    writer.Write(reader.ReadToEnd());
                                }
                            }
                            else
                            {
                                cryptoStream.CopyTo(output);
                            }
                        }
                    }
                }
                catch (CryptographicException)
                {
                    input.Position = 0;
                    output.Position = 0;
                    input.CopyTo(output);
                }
            }
        }

        static void EncryptFile(string originalFilePath, string encryptedFilePath)
        {
            using (FileStream input = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read))
            using (FileStream output = new FileStream(encryptedFilePath, FileMode.Create, FileAccess.Write))
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Key = aesKey;
                    aes.IV = aesIV;

                    using (CryptoStream cryptoStream = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        input.CopyTo(cryptoStream);
                    }
                }
            }
        }

        static Process StartAssociatedProgram(string filePath)
        {
            try
            {
                var info = new ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                };
                return Process.Start(info);
            }
            catch (Exception ex)
            {
                Log($"연결된 프로그램 실행 실패: {ex.Message}");
                return null;
            }
        }

        static void MonitorAndReEncrypt(Process process, string originalFilePath, string backupFilePath, string encryptedFilePath)
        {
            try
            {
                process.WaitForExit();
                Thread.Sleep(2000);

                if (WaitForFileRelease(originalFilePath, TimeSpan.FromMinutes(2)))
                {
                    CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                }
                else
                {
                    Log("파일이 사용 중입니다. 재암호화 실패.");
                }
            }
            catch (Exception ex)
            {
                Log($"모니터링 오류: {ex.Message}");
            }
        }

        static bool WaitForFileRelease(string filePath, TimeSpan timeout)
        {
            DateTime start = DateTime.Now;
            while (DateTime.Now - start < timeout)
            {
                try
                {
                    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        return true;
                    }
                }
                catch
                {
                    Thread.Sleep(2000);
                }
            }
            return false;
        }

        static void CleanupAndReEncrypt(string originalFilePath, string backupFilePath, string encryptedFilePath)
        {
            try
            {
                Log("재암호화 시작");

                EncryptFile(originalFilePath, encryptedFilePath);

                Log("재암호화 완료");

                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        File.Delete(originalFilePath);
                        Log("원본 파일 삭제 완료");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"원본 파일 삭제 실패 (시도 {i + 1}): {ex.Message}");
                        Thread.Sleep(1000);
                    }
                }

                if (File.Exists(originalFilePath))
                {
                    Log("최종적으로 원본 파일 삭제 실패");
                }

                if (File.Exists(backupFilePath))
                {
                    File.Delete(backupFilePath);
                    Log("백업 파일 삭제 완료");
                }
            }
            catch (Exception ex)
            {
                Log($"정리 중 오류: {ex.Message}");
            }
        }

        static void Log(string message)
        {
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(logMessage);

            try
            {
                // 로그 파일 위치는 현재 실행 파일의 폴더로 지정
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "atool.log");
                File.AppendAllText(path, logMessage + Environment.NewLine);
            }
            catch
            {
                // 로그 기록 중 예외 무시
            }
        }
    }
}