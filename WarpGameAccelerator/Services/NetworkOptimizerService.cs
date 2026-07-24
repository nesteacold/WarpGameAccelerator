using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WarpGameAccelerator.Services;

public class NetworkOptimizerService
{
    private const string TCPIP_INTERFACES_KEY = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private readonly Dictionary<string, (int? TcpAckFreq, int? TcpNoDelay)> _registryBackups = new();
    private readonly Dictionary<string, int> _mtuBackups = new();

    public async Task OptimizeAsync()
    {
        await BackupAndOptimizeRegistryAsync();
        await BackupAndOptimizeMtuAsync();
    }

    public async Task RestoreAsync()
    {
        await RestoreRegistryAsync();
        await RestoreMtuAsync();
    }

    private Task BackupAndOptimizeRegistryAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES_KEY, true);
                if (baseKey == null) return;

                foreach (var interfaceName in baseKey.GetSubKeyNames())
                {
                    using var interfaceKey = baseKey.OpenSubKey(interfaceName, true);
                    if (interfaceKey == null) continue;

                    // Read current values
                    var currentAck = interfaceKey.GetValue("TcpAckFrequency") as int?;
                    var currentNoDelay = interfaceKey.GetValue("TCPNoDelay") as int?;

                    // Backup
                    _registryBackups[interfaceName] = (currentAck, currentNoDelay);

                    // Optimize
                    interfaceKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                    interfaceKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Lỗi Optimize Registry: {ex.Message}");
            }
        });
    }

    private Task RestoreRegistryAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES_KEY, true);
                if (baseKey == null) return;

                foreach (var kvp in _registryBackups)
                {
                    var interfaceName = kvp.Key;
                    var backup = kvp.Value;

                    using var interfaceKey = baseKey.OpenSubKey(interfaceName, true);
                    if (interfaceKey == null) continue;

                    // Restore TcpAckFrequency
                    if (backup.TcpAckFreq.HasValue)
                        interfaceKey.SetValue("TcpAckFrequency", backup.TcpAckFreq.Value, RegistryValueKind.DWord);
                    else
                        interfaceKey.DeleteValue("TcpAckFrequency", false);

                    // Restore TCPNoDelay
                    if (backup.TcpNoDelay.HasValue)
                        interfaceKey.SetValue("TCPNoDelay", backup.TcpNoDelay.Value, RegistryValueKind.DWord);
                    else
                        interfaceKey.DeleteValue("TCPNoDelay", false);
                }
                
                _registryBackups.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Lỗi Restore Registry: {ex.Message}");
            }
        });
    }

    private async Task BackupAndOptimizeMtuAsync()
    {
        try
        {
            // First, get all subinterfaces and their MTU
            var output = await RunNetshCommandAsync("interface ipv4 show subinterfaces");
            
            // output looks like:
            //    MTU  MediaSenseState   Bytes In  Bytes Out  Interface
            // ------  ---------------  ---------  ---------  -------------
            // 4294967295                1          0      24197  Loopback Pseudo-Interface 1
            //   1500                1  1028303310  163777717  Wi-Fi
            //   1500                5          0          0  Ethernet
            
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool isData = false;
            
            foreach (var line in lines)
            {
                if (line.StartsWith("---"))
                {
                    isData = true;
                    continue;
                }
                
                if (isData)
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5)
                    {
                        if (int.TryParse(parts[0], out int mtu))
                        {
                            // Interface name can contain spaces, so we join everything after Bytes Out
                            string interfaceName = string.Join(" ", parts, 4, parts.Length - 4);
                            
                            // Skip loopback and already optimized
                            if (!interfaceName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) && 
                                !interfaceName.Contains("Pseudo", StringComparison.OrdinalIgnoreCase))
                            {
                                _mtuBackups[interfaceName] = mtu;
                                
                                // Set MTU to 1420 if not already
                                if (mtu != 1420)
                                {
                                    await RunNetshCommandAsync($"interface ipv4 set subinterface \"{interfaceName}\" mtu=1420 store=persistent");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Lỗi Optimize MTU: {ex.Message}");
        }
    }

    private async Task RestoreMtuAsync()
    {
        try
        {
            foreach (var kvp in _mtuBackups)
            {
                var interfaceName = kvp.Key;
                var backupMtu = kvp.Value;
                
                // Only restore if it was different
                if (backupMtu != 1420)
                {
                    await RunNetshCommandAsync($"interface ipv4 set subinterface \"{interfaceName}\" mtu={backupMtu} store=persistent");
                }
            }
            _mtuBackups.Clear();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Lỗi Restore MTU: {ex.Message}");
        }
    }

    private Task<string> RunNetshCommandAsync(string arguments)
    {
        var tcs = new TaskCompletionSource<string>();
        
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            },
            EnableRaisingEvents = true
        };

        process.Exited += (sender, args) =>
        {
            var result = process.StandardOutput.ReadToEnd();
            process.Dispose();
            tcs.SetResult(result);
        };

        process.Start();
        return tcs.Task;
    }
}
