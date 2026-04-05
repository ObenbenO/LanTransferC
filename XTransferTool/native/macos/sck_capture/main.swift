import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo
import Dispatch

final class FrameWriter: NSObject, SCStreamOutput {
    private var wroteHeader = false
    private let out = FileHandle.standardOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == SCStreamOutputType.screen else { return }
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

let writer = FrameWriter()

Task {
    do {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else {
            exit(2)
        }

        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let config = SCStreamConfiguration()
        config.capturesAudio = false
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.queueDepth = 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 30)

        let stream = SCStream(filter: filter, configuration: config, delegate: nil)
        try stream.addStreamOutput(writer, type: SCStreamOutputType.screen, sampleHandlerQueue: DispatchQueue(label: "sck_capture"))
        try await stream.startCapture()
    } catch {
        let msg = String(describing: error)
        if let data = (msg + "\n").data(using: .utf8) {
            FileHandle.standardError.write(data)
        }
        exit(1)
    }
}

dispatchMain()
