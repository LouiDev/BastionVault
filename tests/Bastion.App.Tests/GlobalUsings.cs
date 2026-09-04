// A WPF test project does not get System.IO in its implicit usings; the settings and shell tests
// touch real temp files, so it is declared once here.
global using System.IO;
