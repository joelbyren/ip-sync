# IpSync — Minecraft Server IP Sync Tool

Keeps friends' hosts files up to date when your server's public IP changes.
Friends run this in **server mode**. You run this in **client mode**.

## Requirements

.NET 10 SDK — https://dotnet.microsoft.com/download

## Quick Start

1. Put `IpSync.cs` anywhere and run:
   ```
   dotnet run IpSync.cs
   ```
   Or compile to a self-contained binary:
   ```
   dotnet publish IpSync.cs -c Release -r linux-x64 --self-contained
   dotnet publish IpSync.cs -c Release -r win-x64   --self-contained
   ```
2. **Friends (Linux only):** run with `sudo`, choose **[S]erver**, follow prompts — config is saved
3. **You:** run normally, choose **[C]lient**, enter token and friends — config is saved
4. After setup, just run with no arguments — no input needed
   ```
   dotnet run IpSync.cs
   ```
   To change config or add friends later:
   ```
   dotnet run IpSync.cs --setup
   ```

## How It Works

- **Server (friend's machine, Linux only):** listens on a port, validates a shared token, reads your public IP from the incoming TCP connection, and writes/updates an entry in `/etc/hosts` like `1.2.3.4  minecraft-home`
- **Client (your machine):** POSTs your token to each friend's server. No IP lookup needed — the server sees your IP from the connection itself.

## Security

- The shared token prevents strangers from triggering a hosts update
- The IP is read server-side from the TCP connection — the client doesn't supply it, so there's nothing to spoof in the payload
- Friends should only run the server while they want to sync, not 24/7
