import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo

final class FrameWriter: NSObject, SCStreamOutput {
    private var wroteHeader = false
    private let out = FileHandle.standardOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen else { return }
        guard let imageBuffer = sampleBuffer.imageBuffer else { return }

        CVPixelBufferLockBaseAddress(imageBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(imageBuffer, .readOnly) }

        let width = CVPixelBufferGetWidth(imageBuffer)
        let height = CVPixelBufferGetHeight(imageBuffer)
        let bytesPerRow = CVPixelBufferGetBytesPerRow(imageBuffer)
        guard let base = CVPixelBufferGetBaseAddress(imageBuffer) else { return }

        if !wroteHeader {
            var w = Int32(width)
            var h = Int32(height)
            var header = Data()
            header.append(Data(bytes: &w, count: 4))
            header.append(Data(bytes: &h, count: 4))
            out.write(header)
            wroteHeader = true
        }

        let rowBytes = width * 4
        for y in 0..<height {
            let src = base.advanced(by: y * bytesPerRow)
            let d = Data(bytes: src, count: rowBytes)
            out.write(d)
        }
    }
}

@main
struct SckCaptureMain {
    static func main() async {
        let writer = FrameWriter()
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            guard let display = content.displays.first else {
                exit(2)
            }

            let filter = SCContentFilter(display: display, excludingWindows: [], exceptingApplications: [])
            let config = SCStreamConfiguration()
            config.capturesAudio = false
            config.pixelFormat = kCVPixelFormatType_32BGRA
            config.queueDepth = 2
            config.minimumFrameInterval = CMTime(value: 1, timescale: 30)

            let stream = SCStream(filter: filter, configuration: config, delegate: nil)
            try stream.addStreamOutput(writer, type: .screen, sampleHandlerQueue: DispatchQueue(label: "sck_capture"))
            try await stream.startCapture()

            RunLoop.current.run()
        } catch {
            exit(1)
        }
    }
}

