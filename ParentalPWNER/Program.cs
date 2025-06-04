//	Copyright (c) 2025 David Hornemark
//
//	This software is provided 'as-is', without any express or implied warranty. In
//	no event will the authors be held liable for any damages arising from the use
//	of this software.
//
//	Permission is granted to anyone to use this software for any purpose,
//	including commercial applications, and to alter it and redistribute it freely,
//	subject to the following restrictions:
//
//	1. The origin of this software must not be misrepresented; you must not claim
//	that you wrote the original software. If you use this software in a product,
//	an acknowledgment in the product documentation would be appreciated but is not
//	required.
//
//	2. Altered source versions must be plainly marked as such, and must not be
//	misrepresented as being the original software.
//
//	3. This notice may not be removed or altered from any source distribution.
//
//  =============================================================================

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Management;
using Microsoft.Win32;
using IWshRuntimeLibrary;
using System.Reflection;

class Program
{
    const double DownloadThresholdMbps = 1; // Mb/s

    static bool spoofname = true;
    static bool checkforbandwidth = true;
    static async Task Main()
    {
        Console.WriteLine("Do you want to spoof your name aswell?");

        var response = Console.ReadLine().ToUpper();

        spoofname = response.Contains("Y");

        if (spoofname)
        {
            Console.WriteLine("Spoofing name.");
        }
        else
        {
            Console.WriteLine("Not spoofing name.");
        }

        Console.WriteLine("Do you want to check for bandwidth aswell?");

        response = Console.ReadLine().ToUpper();

        checkforbandwidth = response.Contains("Y");

        if (checkforbandwidth)
        {
            Console.WriteLine("Checking For Bandwith.");
        }
        else
        {
            Console.WriteLine("NOT Checking For Bandwith.");
        }

        while (true)
        {
            if (await CheckConnection())
            {
                await ConnectionGood();
            }
            else
            {
                Console.WriteLine("Triggering identity change.");
                BecomeNewComputer();
                Console.WriteLine("Waiting 10 seconds before retrying...");

                await ConnectionBad();
            }
        }
    }

    #region ConnectChecks

    /// <summary>
    /// Pings the host to check connectivity.
    /// </summary>
    /// <param name="host">hostname</param>
    /// <returns>if the ping was a success</returns>
    static bool PingHost(string host)
    {
        try
        {
            Ping ping = new Ping();
            PingReply reply = ping.Send(host, 3000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// Measure the download speed. This makes sure ratelimiting will trigger a change of identity.
    /// </summary>
    /// <param name="testUrl">the url to download the data.</param>
    /// <returns>download speed in Mbps</returns>
    static async Task<double> MeasureDownloadSpeedMbps(string testUrl)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        var stopwatch = Stopwatch.StartNew();
        var data = await httpClient.GetByteArrayAsync(testUrl);
        stopwatch.Stop();

        long totalBytes = data.Length;
        double seconds = stopwatch.Elapsed.TotalSeconds;
        double bits = totalBytes * 8;

        double mbps = bits / (seconds * 1024 * 1024);
        return mbps;
    }

    static async Task<bool> CheckConnection()
    {
        Console.WriteLine("nbg1-speed.hetzner.com...");
        bool pingOK = PingHost("nbg1-speed.hetzner.com");

        if (!pingOK)
        {
            Console.WriteLine("Ping failed.");
            return false;
        }

        Console.WriteLine("Ping OK.");
        if (checkforbandwidth)
        {
            Console.WriteLine("Testing speed...");
            string url = "http://ipv4.download.thinkbroadband.com/1MB.zip";
            double speed = await MeasureDownloadSpeedMbps(url);
            Console.WriteLine($"Speed Download: {speed:F2} Mbps");

            return speed >= DownloadThresholdMbps;
        }
        return true;
    }

    #endregion

    #region Spoofing

    /// <summary>
    /// Spoofs the computer's identity by changing the MAC address and computer name, then flushing network caches.
    /// </summary>
    static void BecomeNewComputer()
    {
        ChangeMacAddress(); // via registry + disable/enable
        FlushNetworkCaches();
        if(spoofname)
            ChangeComputerName(); // requires reboot to fully take effect
    }

    /// <summary>
    /// Flushes various network caches to reset the network state.
    /// </summary>
    static void FlushNetworkCaches()
    {
        RunNetCommand("ipconfig /release", "Releasing IP...");
        RunNetCommand("ipconfig /flushdns", "Flushing DNS...");
        RunNetCommand("arp -d *", "Clearing ARP cache...");
        RunNetCommand("ipconfig /renew", "Renewing IP...");
    }

    /// <summary>
    /// Changes Mac adress to a randomly generated one.
    /// </summary>
    static void ChangeMacAddress()
    {
        try
        {
            string newMac = GenerateRandomMac();
            Console.WriteLine("Generated new MAC: " + newMac);

            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .FirstOrDefault();

            Console.WriteLine("Using adapter: " + (adapter?.Name ?? "None"));

            if (adapter == null)
            {
                Console.WriteLine("No active network adapter found.");
                return;
            }

            string nicId = adapter.Id;
            string regPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\";

            RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
            foreach (var subkeyName in baseKey.GetSubKeyNames())
            {
                var subkey = baseKey.OpenSubKey(subkeyName, writable: true);
                if (subkey?.GetValue("NetCfgInstanceId")?.ToString() == nicId)
                {
                    subkey.SetValue("NetworkAddress", newMac);
                    Console.WriteLine("MAC set in registry. Restarting adapter...");

                    string name = adapter.Name;
                    RunNetsh($"interface set interface \"{name}\" admin=disable");
                    Thread.Sleep(2000);
                    RunNetsh($"interface set interface \"{name}\" admin=enable");

                    Console.WriteLine("Adapter restarted.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to change MAC: " + ex.Message);
        }
    }

    /// <summary>
    /// Changes the computer name to a random value. Requires restart, so the user will have to restart manually if they still get blocked.
    /// </summary>
    static void ChangeComputerName()
    {
        try
        {
            string newName = "PC-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
            Console.WriteLine("Changing computer name to: " + newName);

            using (var managementClass = new ManagementClass("Win32_ComputerSystem"))
            {
                managementClass.Scope.Options.EnablePrivileges = true;
                foreach (ManagementObject obj in managementClass.GetInstances())
                {
                    object[] args = { newName };
                    var result = (uint)obj.InvokeMethod("Rename", args);
                    Console.WriteLine("Rename result: " + result + " (reboot required)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to change computer name: " + ex.Message);
        }
    }


    #endregion

    #region Utils

    /// <summary>
    /// Generates a random mac adress
    /// </summary>
    /// <returns>mac adress.</returns>
    static string GenerateRandomMac()
    {
        var rand = new Random();
        byte[] mac = new byte[6];
        rand.NextBytes(mac);
        mac[0] = (byte)((mac[0] & 0xFE) | 0x02); // locally administered
        return string.Join("", mac.Select(b => b.ToString("X2")));
    }

    private static async Task ConnectionGood()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Connection is good.");
        Console.ForegroundColor = ConsoleColor.White;
        await Task.Delay(5000);
        Console.Clear();
    }

    private static async Task ConnectionBad()
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Connection Failed.");
        Console.ForegroundColor = ConsoleColor.White;
        await Task.Delay(10000);
        Console.Clear();
    }

    #endregion

    #region Commands

    /// <summary>
    /// Runs commands with elevated privileges.
    /// </summary>
    /// <param name="command">command that will be executed.</param>
    /// <param name="description">what will be printed to console.</param>
    static void RunNetCommand(string command, string description)
    {
        Console.WriteLine(description);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = true
        })?.WaitForExit();
    }

    /// <summary>
    /// Runs a netsh command with the provided arguments.
    /// </summary>
    /// <param name="args">arguments</param>
    static void RunNetsh(string args)
    {
        var p = new Process();
        p.StartInfo.FileName = "netsh";
        p.StartInfo.Arguments = args;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.UseShellExecute = false;
        p.Start();
        p.WaitForExit();
    }

    #endregion
}
