namespace LostTech.TensorFlow {
    using System;
    using System.IO;
    using LostTech.IO.Links;
    using SharpCompress.Common;
    using SharpCompress.Readers;

    static class Archive {
        public static void Extract(string archive, string target) {
            const bool overwrite = true;

            Directory.CreateDirectory(target);

            void WriteSymbolicLink(string symlink, string pointTo) {
                if (Path.IsPathRooted(pointTo))
                    throw new UnauthorizedAccessException();
                pointTo = Path.Combine(Path.GetDirectoryName(symlink), pointTo);
                pointTo = Path.GetFullPath(pointTo);

                if (!PathEx.IsNested(target, symlink))
                    throw new UnauthorizedAccessException();

                if (overwrite) {
                    if (File.Exists(symlink)) File.Delete(symlink);
                    if (Directory.Exists(symlink)) Directory.Delete(symlink);
                }

                if (File.Exists(pointTo)) {
                    Symlink.CreateForFile(filePath: pointTo, symlink: symlink);
                } else if (Directory.Exists(pointTo)) {
                    Symlink.CreateForDirectory(directoryPath: pointTo, symlink: symlink);
                } else {
                    Directory.CreateDirectory(pointTo);
                    try {
                        Symlink.CreateForDirectory(directoryPath: pointTo, symlink: symlink);
                    } finally {
                        Directory.Delete(pointTo);
                    }
                }
            }

            var options = new ExtractionOptions {
                ExtractFullPath = true,
                Overwrite = overwrite,
                WriteSymbolicLink = WriteSymbolicLink,
            };
            using var stream = File.OpenRead(archive);
            using var reader = ReaderFactory.Open(stream);
            while (reader.MoveToNextEntry()) {
                if (reader.Entry.IsDirectory) continue;

                reader.WriteEntryToDirectory(target, options);
            }
        }
    }
}