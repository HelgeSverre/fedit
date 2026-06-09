namespace Fedit

open System

/// Raw image payload plus placement metadata.
type ImageData =
    {
        /// Raw bytes of the image file (PNG, JPEG, etc.).
        Bytes: byte[]
        /// Desired width in terminal cells (0 = auto from image aspect).
        CellWidth: int
        /// Desired height in terminal cells (0 = auto from image aspect).
        CellHeight: int
        /// Horizontal cell offset for placement.
        Left: int
        /// Vertical cell offset for placement.
        Top: int
    }

/// Abstract terminal image protocol. Each concrete implementation handles
/// encoding, transmission, and cleanup for one protocol (Kitty, iTerm2,
/// Sixel, etc.).
type ImageProtocol =
    {
        /// Unique identifier for this protocol.
        Kind: ImageProtocolKind
        /// Encode and write a single image frame to the terminal writer.
        Transmit: System.IO.TextWriter -> ImageData -> unit
        /// Clear all images currently displayed by this protocol.
        Clear: System.IO.TextWriter -> unit
        /// Send a query sequence and return true if the terminal responds
        /// positively within the timeout. The caller must drain the input
        /// stream before and after this call.
        QuerySupport: System.IO.TextWriter -> (int -> System.ConsoleKeyInfo option) -> TimeSpan -> bool
    }

[<RequireQualifiedAccess>]
module ImageProtocol =
    /// No-op protocol for terminals that don't support inline images.
    let none: ImageProtocol =
        { Kind = ImageNone
          Transmit = fun _ _ -> ()
          Clear = fun _ -> ()
          QuerySupport = fun _ _ _ -> false }
