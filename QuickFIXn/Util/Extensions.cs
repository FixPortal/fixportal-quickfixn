#region Using Directives

using System.IO;

#endregion

namespace QuickFix.Util;

	public static class Extensions
	{
		public static bool IsNotLocked(this FileInfo file)
		{
			return !file.IsLocked();
		}

		public static bool IsLocked(this FileInfo file)
		{
			FileStream? stream = null;

			try
			{
				if (File.Exists(file.FullName))
				{
					stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
				}
				else
				{
					return false;
				}
			}
			catch (IOException)
			{
				return true;
			}
			finally
			{
				if (stream != null)
					stream.Close();
			}

			return false;
		}
	}
