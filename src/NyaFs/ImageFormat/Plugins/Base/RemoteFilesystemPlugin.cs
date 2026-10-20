using NyaFs.Remote;
using System;

namespace NyaFs.ImageFormat.Plugins.Base
{
    /// <summary>
    /// Base class for plugins that provide remote filesystem transports
    /// (ssh/scp/sftp/ftp/...). Implementations own their protocol libraries.
    /// </summary>
    public abstract class RemoteFilesystemPlugin : NyaPlugin
    {
        protected RemoteFilesystemPlugin(string Name) : base(Name, "remote")
        {
        }

        /// <summary>Protocol names handled by this plugin (lower case recommended).</summary>
        public abstract string[] Protocols { get; }

        /// <summary>
        /// Create a transport for the given protocol. HostPort is "host" or "host:port".
        /// </summary>
        public abstract IRemoteFs Create(string Protocol, string HostPort, string Login, string Password);
    }
}
