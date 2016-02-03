﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Windows.Networking;
using SharpCaster.Models;
using SharpCaster.Interfaces;
using SharpCaster.Services;

namespace SharpCaster
{
    public class DeviceLocator
    {
        private const string MulticastHostName = "239.255.255.250";
        private const string MulticastPort = "1900";
        private TimeSpan _timeOut;
        private readonly XmlSerializer _xmlSerializer;
        public ISocketService _socketService;
        private List<Chromecast> _discoveredDevices;
        
        public DeviceLocator()
        {
            _xmlSerializer = new XmlSerializer(typeof(DiscoveryRoot));
            _socketService = new SocketService();
        }

        public async Task<IEnumerable<Chromecast>> LocateDevicesAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                timeout = TimeSpan.FromMilliseconds(2000);

            _timeOut = timeout;

            _discoveredDevices = new List<Chromecast>();

            var multicastIP = new HostName(MulticastHostName);
            _socketService.MessageReceived += MulticastResponseReceived;
            using (_socketService.Initialize())
            {
                await _socketService.BindEndpointAsync(null, string.Empty);
                _socketService.JoinMulticastGroup(multicastIP);
                var start = DateTime.Now;
                do
                {
                    using (var writer = await _socketService.GetOutputWriterAsync(multicastIP, MulticastPort))
                    { 
                        const string request = "M-SEARCH * HTTP/1.1\r\n" +
                                               "HOST:" + MulticastHostName + ":" + MulticastPort + "\r\n" +
                                               "ST:SsdpSearch:all\r\n" +
                                               "MAN:\"ssdp:discover\"\r\n" +
                                               "MX:3\r\n\r\n\r\n";
                        writer.WriteString(request);
                        await writer.StoreAsync();
                    }
                }
                while (DateTime.Now.Subtract(start) < timeout);
            }
            _socketService.MessageReceived -= MulticastResponseReceived;
            return await FilterChromeCasts(_discoveredDevices);

        }

        private void MulticastResponseReceived(object sender, string receivedString)
        {
            if (string.IsNullOrWhiteSpace(receivedString)) return;
            var location = receivedString.Substring(receivedString.ToLower().IndexOf("location:", StringComparison.Ordinal) + 9);
            receivedString = location.Substring(0, location.IndexOf("\r", StringComparison.Ordinal)).Trim();

            Uri uri;
            if (!Uri.TryCreate(receivedString, UriKind.Absolute, out uri)) return;
            if (!_discoveredDevices.Any(x => x.DeviceUri.Equals(uri)))
            {
                _discoveredDevices.Add(new Chromecast { DeviceUri = uri });
            }
        }

        private async Task<IEnumerable<Chromecast>> FilterChromeCasts(IEnumerable<Chromecast> discoveredDevices)
        {
            var chromecasts = new List<Chromecast>();
            var possibleChromecasts = discoveredDevices.Where(s => s.DeviceUri.AbsoluteUri.EndsWith("/ssdp/device-desc.xml")).ToList();
            foreach (var endpoint in possibleChromecasts.Where(endpoint => !chromecasts.Contains(endpoint)))
            {
                if (await GetChromecastName(endpoint))
                {
                    chromecasts.Add(endpoint);
                }
            }

            return chromecasts;
        }

        private async Task<bool> GetChromecastName(Chromecast chromecast)
        {
            var res = await _socketService.GetStringAsync(chromecast.DeviceUri, _timeOut);

            if (string.IsNullOrWhiteSpace(res)) return false;
            var stringReader = new StringReader(res);
            var data = (DiscoveryRoot)_xmlSerializer.Deserialize(stringReader);
            if (!data.Device.DeviceType.ToLower().Equals("urn:dial-multiscreen-org:device:dial:1")) return false;
            chromecast.FriendlyName = data.Device.FriendlyName;
            return true;
        }

    }
}