// WPF projects do not get System.IO in their implicit usings, and the app touches files in a
// dozen places (settings, logs, keyfiles, staging paths). Declaring it once here keeps the
// per-file using lists about the app's own namespaces.
global using System.IO;
