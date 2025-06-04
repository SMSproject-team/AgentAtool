using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

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

                // 파일 시작 시간 기록
                DateTime originalLastWrite = File.GetLastWriteTime(originalFilePath);

                Process process = StartAssociatedProgram(originalFilePath);

                if (process != null)
                {
                    Task.Run(() => MonitorAndReEncrypt(process, originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite));
                }
                else
                {
                    // 프로그램 실행 실패 시 파일 변경 모니터링으로 대체
                    Log("연결된 프로그램을 실행할 수 없습니다. 파일 변경 모니터링으로 전환합니다.");
                    Task.Run(() => MonitorFileChanges(originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite));
                }

                Log("파일이 복호화되었습니다. 편집 후 프로그램을 닫으면 자동으로 재암호화됩니다.");
                Log("프로그램을 강제 종료하려면 Ctrl+C를 누르세요.");

                // 메인 스레드에서 사용자 입력 대기 (강제 종료 방지)
                Console.CancelKeyPress += (sender, e) => {
                    e.Cancel = true;
                    Log("강제 종료 신호 감지. 재암호화를 진행합니다...");
                    CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                    Environment.Exit(0);
                };

                // 무한 대기 (백그라운드 작업이 완료될 때까지)
                while (File.Exists(originalFilePath))
                {
                    Thread.Sleep(5000);
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
                Process process = Process.Start(info);

                // 프로세스가 실제로 시작되었는지 확인
                if (process != null && !process.HasExited)
                {
                    Log($"프로그램이 시작되었습니다: PID {process.Id}");
                    return process;
                }
                return null;
            }
            catch (Exception ex)
            {
                Log($"연결된 프로그램 실행 실패: {ex.Message}");
                return null;
            }
        }

        static void MonitorAndReEncrypt(Process process, string originalFilePath, string backupFilePath, string encryptedFilePath, DateTime originalLastWrite)
        {
            try
            {
                Log($"프로세스 모니터링 시작: PID {process.Id}");

                // 프로세스 종료 대기
                process.WaitForExit();
                Log("프로세스가 종료되었습니다.");

                // 추가 대기 시간
                Thread.Sleep(3000);

                // 파일 해제 대기 (타임아웃 증가)
                if (WaitForFileRelease(originalFilePath, TimeSpan.FromMinutes(5)))
                {
                    CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                }
                else
                {
                    Log("파일이 사용 중입니다. 파일 변경 모니터링으로 전환합니다.");
                    MonitorFileChanges(originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite);
                }
            }
            catch (Exception ex)
            {
                Log($"프로세스 모니터링 오류: {ex.Message}");
                // 오류 발생 시 파일 변경 모니터링으로 대체
                MonitorFileChanges(originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite);
            }
        }

        static void MonitorFileChanges(string originalFilePath, string backupFilePath, string encryptedFilePath, DateTime originalLastWrite)
        {
            Log("파일 변경 모니터링을 시작합니다.");

            DateTime lastCheckTime = DateTime.Now;
            bool fileWasModified = false;

            while (File.Exists(originalFilePath))
            {
                try
                {
                    DateTime currentLastWrite = File.GetLastWriteTime(originalFilePath);

                    // 파일이 수정되었는지 확인
                    if (currentLastWrite > originalLastWrite.AddSeconds(1))
                    {
                        fileWasModified = true;
                        originalLastWrite = currentLastWrite;
                        lastCheckTime = DateTime.Now;
                        Log("파일 변경이 감지되었습니다.");
                    }

                    // 파일이 수정된 후 일정 시간 동안 변경이 없으면 재암호화 시작
                    if (fileWasModified && (DateTime.Now - lastCheckTime).TotalMinutes > 2)
                    {
                        if (WaitForFileRelease(originalFilePath, TimeSpan.FromMinutes(3)))
                        {
                            CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                            break;
                        }
                        else
                        {
                            Log("파일이 여전히 사용 중입니다. 계속 모니터링합니다.");
                            lastCheckTime = DateTime.Now; // 다시 대기
                        }
                    }

                    Thread.Sleep(5000); // 5초마다 확인
                }
                catch (Exception ex)
                {
                    Log($"파일 모니터링 오류: {ex.Message}");
                    Thread.Sleep(10000);
                }
            }
        }

        static bool WaitForFileRelease(string filePath, TimeSpan timeout)
        {
            Log($"파일 해제 대기 중... (최대 {timeout.TotalMinutes:F0}분)");
            DateTime start = DateTime.Now;
            int attempts = 0;

            while (DateTime.Now - start < timeout)
            {
                try
                {
                    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Log($"파일 해제 확인됨 (시도 {attempts + 1}회)");
                        return true;
                    }
                }
                catch
                {
                    attempts++;
                    if (attempts % 10 == 0) // 매 10번째 시도마다 로그
                    {
                        Log($"파일 해제 대기 중... (시도 {attempts}회)");
                    }
                    Thread.Sleep(3000); // 3초 간격으로 확인
                }
            }

            Log($"파일 해제 타임아웃 (총 {attempts}회 시도)");
            return false;
        }

        static void CleanupAndReEncrypt(string originalFilePath, string backupFilePath, string encryptedFilePath)
        {
            try
            {
                Log("재암호화 시작");

                // 재암호화 시도
                int maxRetries = 3;
                bool encryptSuccess = false;

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        EncryptFile(originalFilePath, encryptedFilePath);
                        encryptSuccess = true;
                        Log("재암호화 완료");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"재암호화 실패 (시도 {i + 1}): {ex.Message}");
                        if (i < maxRetries - 1)
                        {
                            Thread.Sleep(2000);
                        }
                    }
                }

                if (!encryptSuccess)
                {
                    Log("재암호화에 실패했습니다. 백업 파일을 복원합니다.");
                    if (File.Exists(backupFilePath))
                    {
                        File.Move(backupFilePath, encryptedFilePath);
                        Log("백업 파일로부터 복원 완료");
                    }
                    return;
                }

                // 원본 파일 삭제
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        // 파일 속성 제거 후 삭제
                        File.SetAttributes(originalFilePath, FileAttributes.Normal);
                        File.Delete(originalFilePath);
                        Log("원본 파일 삭제 완료");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"원본 파일 삭제 실패 (시도 {i + 1}): {ex.Message}");
                        Thread.Sleep(2000);
                    }
                }

                if (File.Exists(originalFilePath))
                {
                    Log("경고: 원본 파일 삭제에 실패했습니다. 수동으로 삭제해 주세요.");
                }

                // 백업 파일 삭제
                if (File.Exists(backupFilePath))
                {
                    try
                    {
                        File.Delete(backupFilePath);
                        Log("백업 파일 삭제 완료");
                    }
                    catch (Exception ex)
                    {
                        Log($"백업 파일 삭제 실패: {ex.Message}");
                    }
                }

                Log("모든 작업이 완료되었습니다.");
            }
            catch (Exception ex)
            {
                Log($"정리 중 오류: {ex.Message}");

                // 최후의 복구 시도
                try
                {
                    if (File.Exists(backupFilePath) && !File.Exists(encryptedFilePath))
                    {
                        File.Move(backupFilePath, encryptedFilePath);
                        Log("오류 상황에서 백업 파일 복원 완료");
                    }
                }
                catch (Exception restoreEx)
                {
                    Log($"백업 파일 복원 실패: {restoreEx.Message}");
                }
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