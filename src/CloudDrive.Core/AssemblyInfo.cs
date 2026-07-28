using System.Runtime.CompilerServices;

// A handful of helpers are internal because nothing outside Core should call them, but they carry
// the logic most worth testing directly: numeric version comparison (string comparison would order
// 1.10 before 1.9 and silently stop offering updates) and maintenance-window arithmetic (which has
// to handle a window that crosses midnight). Testing them through their public callers would mean
// standing up a GitHub feed and a clock.
[assembly: InternalsVisibleTo("CloudDrive.Tests")]
