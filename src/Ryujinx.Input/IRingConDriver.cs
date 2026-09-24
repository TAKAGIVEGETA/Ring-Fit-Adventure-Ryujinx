using System;

namespace Ryujinx.Input
{
    /// <summary>
    /// Represent a host driver for a physical Ring-Con attached to a Joy-Con.
    /// </summary>
    public interface IRingConDriver : IDisposable
    {
        /// <summary>
        /// True when a physical Ring-Con is connected and reporting data.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Normalized Ring-Con force, from -1 (fully pulled) to 1 (fully squeezed).
        /// </summary>
        float Force { get; }

        /// <summary>
        /// Start or stop polling the physical Ring-Con.
        /// </summary>
        /// <param name="active">True to poll the Ring-Con</param>
        void SetActive(bool active);
    }
}
