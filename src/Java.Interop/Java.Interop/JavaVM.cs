using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Java.Interop
{
	delegate int DestroyJavaVMDelegate (JavaVMSafeHandle javavm);
	delegate int GetEnvDelegate (JavaVMSafeHandle javavm, out IntPtr envptr, int version);
	delegate int AttachCurrentThreadDelegate (JavaVMSafeHandle javavm, out IntPtr env, ref JavaVMThreadAttachArgs args);
	delegate int DetachCurrentThreadDelegate (JavaVMSafeHandle javavm);
	delegate int AttachCurrentThreadAsDaemonDelegate (JavaVMSafeHandle javavm, out IntPtr env, IntPtr args);

	struct JavaVMInterface {
		public IntPtr reserved0;
		public IntPtr reserved1;
		public IntPtr reserved2;

		public DestroyJavaVMDelegate DestroyJavaVM; // jint       (*DestroyJavaVM)(JavaVM*);
		public AttachCurrentThreadDelegate AttachCurrentThread;
		public DetachCurrentThreadDelegate DetachCurrentThread;
		public GetEnvDelegate GetEnv;
		public AttachCurrentThreadAsDaemonDelegate AttachCurrentThreadAsDaemon; //jint        (*AttachCurrentThreadAsDaemon)(JavaVM*, JNIEnv**, void*);
	}

	public enum JniVersion {
		// v1_1    = 0x00010001,
		v1_2    = 0x00010002,
		v1_4    = 0x00010004,
		v1_6	= 0x00010006,
	}

	struct JavaVMThreadAttachArgs {
		public  JniVersion 	        version;    /*		 must be >= JNI_VERSION_1_2 */
		public  IntPtr              name;       /*		 NULL or name of thread as modified UTF-8 str */
		public  IntPtr              group;      /*		 global ref of a ThreadGroup object, or NULL */
	}

	public sealed class JavaVMSafeHandle : SafeHandle {

		JavaVMSafeHandle ()
			: base (IntPtr.Zero, ownsHandle:false)
		{
		}

		public JavaVMSafeHandle (IntPtr handle)
			: this ()
		{
			SetHandle (handle);
		}

		public override bool IsInvalid {
			get {return handle == IntPtr.Zero;}
		}

		internal IntPtr Handle {
			get {return base.handle;}
		}

		protected override bool ReleaseHandle ()
		{
			return false;
		}

		internal unsafe JavaVMInterface CreateInvoker ()
		{
			IntPtr p = Marshal.ReadIntPtr (handle);
			return (JavaVMInterface) Marshal.PtrToStructure (p, typeof(JavaVMInterface));
		}

		public override string ToString ()
		{
			return string.Format ("{0}(0x{1})", GetType ().FullName, handle.ToString ("x"));
		}
	}

	public class JavaVMOptions {

		public  bool        TrackIDs                    {get; set;}
		public  bool        DestroyVMOnDispose          {get; set;}

		public  JavaVMSafeHandle            VMHandle            {get; set;}
		public  JniEnvironmentSafeHandle    EnvironmentHandle   {get; set;}

		public JavaVMOptions ()
		{
		}
	}

	public abstract partial class JavaVM : IDisposable
	{

		static ConcurrentDictionary<IntPtr, JavaVM>     JavaVMs = new ConcurrentDictionary<IntPtr, JavaVM> ();

		public static IEnumerable<JavaVM> GetRegisteredJavaVMs ()
		{
			return JavaVMs.Values;
		}

		public static JavaVM GetRegisteredJavaVM (JavaVMSafeHandle handle)
		{
			JavaVM vm;
			return JavaVMs.TryGetValue (handle.DangerousGetHandle (), out vm)
				? vm
				: null;
		}

		static JavaVM current;
		public static JavaVM Current {
			get {
				if (current != null)
					return current;
				JavaVM  c       = null;
				int     count   = 0;
				foreach (var vm in JavaVMs.Values) {
					if (count++ == 0)
						c = vm;
				}
				if (count == 0)
					throw new InvalidOperationException ("No JavaVM has been created. Please use Java.Interop.JreVMBuilder.CreateJreVM().");
				if (count > 1)
					throw new NotSupportedException (string.Format ("Found {0} JavaVMs. Don't know which to use. Use JavaVM.SetCurrent().", count));
				return current = c;
			}
		}

		public static void SetCurrent (JavaVM newCurrent)
		{
			if (newCurrent == null)
				throw new ArgumentNullException ("newCurrent");
			JavaVMs.TryAdd (newCurrent.SafeHandle.DangerousGetHandle (), newCurrent);
			current = newCurrent;
		}

		ConcurrentDictionary<IntPtr, JniEnvironment>    Environments = new ConcurrentDictionary<IntPtr, JniEnvironment> ();

		ConcurrentDictionary<SafeHandle, IDisposable>   TrackedInstances;

		JavaVMInterface                                 Invoker;
		bool                                            DestroyVM;

		int                                             GrefCount;
		int                                             LrefCount;
		int                                             WgrefCount;

		public  JavaVMSafeHandle                        SafeHandle      {get; private set;}

		protected JavaVM (JavaVMOptions options)
		{
			if (options == null)
				throw new ArgumentNullException ("options");
			if (options.VMHandle == null)
				throw new ArgumentException ("options.VMHandle is null", "options");
			if (options.VMHandle.IsInvalid)
				throw new ArgumentException ("options.VMHandle is not valid.", "options");

			TrackIDs     = options.TrackIDs;
			DestroyVM    = options.DestroyVMOnDispose;

			SafeHandle  = options.VMHandle;
			Invoker     = SafeHandle.CreateInvoker ();

			if (current == null)
				current = this;

			if (options.EnvironmentHandle != null) {
				var env = new JniEnvironment (options.EnvironmentHandle, this);
				Environments.TryAdd (env.SafeHandle.DangerousGetHandle (), env);
			}

			JavaVMs.TryAdd (SafeHandle.DangerousGetHandle (), this);
		}

		~JavaVM ()
		{
			Dispose (false);
		}

		public override string ToString ()
		{
			return string.Format ("{0}(0x{1})", GetType ().FullName, SafeHandle.DangerousGetHandle ().ToString ("x"));
		}

		public void Dispose ()
		{
			Dispose (true);
		}

		protected virtual void Dispose (bool disposing)
		{
			if (SafeHandle == null)
				return;

			if (current == this)
				current = null;

			ClearTrackedReferences ();
			JavaVM _;
			JavaVMs.TryRemove (SafeHandle.DangerousGetHandle (), out _);
			if (DestroyVM)
				DestroyJavaVM ();
			SafeHandle.Dispose ();
			SafeHandle = null;
		}

		public void AttachCurrentThread (string name = null, JniReferenceSafeHandle group = null)
		{
			var threadArgs = new JavaVMThreadAttachArgs () {
				version = JniVersion.v1_2,
			};
			try {
				if (name != null)
					threadArgs.name = Marshal.StringToHGlobalAnsi (name);
				if (group != null)
					threadArgs.group = group.DangerousGetHandle ();
				IntPtr jnienv;
				int r = Invoker.AttachCurrentThread (SafeHandle, out jnienv, ref threadArgs);
				if (r != 0)
					throw new NotSupportedException ("AttachCurrentThread returned " + r);
				Environments.TryAdd (jnienv, new JniEnvironment (new JniEnvironmentSafeHandle (jnienv), this));
			} finally {
				Marshal.FreeHGlobal (threadArgs.name);
			}
		}

		public void DestroyJavaVM ()
		{
			Invoker.DestroyJavaVM (SafeHandle);
		}

		public virtual Exception GetExceptionForThrowable (JniLocalReference value)
		{
			using (var s = JniEnvironment.Current.Object_toString.CallVirtualObjectMethod (value)) {
				return new JniException (JniStrings.ToString (s) ?? "JNI error: no message provided");
			}
		}

		public int LocalReferenceCount {
			get {return LrefCount;}
		}

		public int GlobalReferenceCount {
			get {return GrefCount;}
		}

		public int WeakGlobalReferenceCount {
			get {return WgrefCount;}
		}

		protected internal virtual void LogCreateLocalRef (JniLocalReference value)
		{
			if (value == null || value.IsInvalid)
				return;
			Interlocked.Increment (ref LrefCount);
		}

		protected internal virtual void LogCreateLocalRef (JniLocalReference value, JniReferenceSafeHandle sourceValue)
		{
			if (value == null || value.IsInvalid)
				return;
			Interlocked.Increment (ref LrefCount);
		}

		protected internal virtual void LogDestroyLocalRef (IntPtr value)
		{
			if (value == IntPtr.Zero)
				return;
			Interlocked.Decrement (ref LrefCount);
		}

		protected internal virtual void LogCreateGlobalRef (JniGlobalReference value, JniReferenceSafeHandle sourceValue)
		{
			if (value == null || value.IsInvalid)
				return;
			Interlocked.Increment (ref GrefCount);
		}

		protected internal virtual void LogDestroyGlobalRef (IntPtr value)
		{
			if (value == IntPtr.Zero)
				return;
			Interlocked.Decrement (ref GrefCount);
		}

		protected internal virtual void LogCreateWeakGlobalRef (JniWeakGlobalReference value, JniReferenceSafeHandle sourceValue)
		{
			if (value == null || value.IsInvalid)
				return;
			Interlocked.Increment (ref WgrefCount);
		}

		protected internal virtual void LogDestroyWeakGlobalRef (IntPtr value)
		{
			if (value == IntPtr.Zero)
				return;
			Interlocked.Decrement (ref WgrefCount);
		}

		public bool TrackIDs {
			get {
				return TrackedInstances != null;
			}
			private set {
				TrackedInstances        = new ConcurrentDictionary<SafeHandle, IDisposable> ();
			}
		}

		internal void Track (SafeHandle key, IDisposable value)
		{
			if (TrackedInstances != null)
				TrackedInstances.TryAdd (key, value);
		}

		internal void UnTrack (SafeHandle key)
		{
			if (TrackedInstances != null) {
				IDisposable _;
				TrackedInstances.TryRemove (key, out _);
			}
		}

		void ClearTrackedReferences ()
		{
			if (TrackedInstances != null) {
				foreach (var k in TrackedInstances.Keys.ToList ()) {
					IDisposable d;
					if (TrackedInstances.TryRemove (k, out d))
						d.Dispose ();
				}
			}
			foreach (var env in Environments.Values)
				env.Dispose ();
			Environments.Clear ();
		}
	}

	partial class JavaVM {

		Dictionary<int, WeakReference>  RegisteredInstances = new Dictionary<int, WeakReference>();

		internal void RegisterObject (int key, IJavaObject value)
		{
			lock (RegisteredInstances)
				if (!RegisteredInstances.ContainsKey (key))
					RegisteredInstances.Add (key, new WeakReference (value, trackResurrection:true));
		}

		internal void UnRegisterObject (int key, IJavaObject value)
		{
			lock (RegisteredInstances) {
				WeakReference               wv;
				IJavaObject                 t;
				if (RegisteredInstances.TryGetValue (key, out wv) &&
						(t = (IJavaObject) wv.Target) != null &&
						object.ReferenceEquals (value, t))
					RegisteredInstances.Remove (key);
			}
		}

		public IJavaObject GetObject (JniReferenceSafeHandle jniHandle, JniHandleOwnership transfer)
		{
			if (jniHandle == null)
				return null;
			if (jniHandle.IsInvalid)
				return null;
			try {
				return GetObject (jniHandle.DangerousGetHandle ());
			} finally {
				JniHandles.Dispose (jniHandle, transfer);
			}
		}

		public IJavaObject GetObject (IntPtr jniHandle)
		{
			if (jniHandle == IntPtr.Zero)
				return null;
			int key;
			using (var h = new JniInvocationHandle (jniHandle))
				key = JniSystem.IdentityHashCode (h);
			lock (RegisteredInstances) {
				WeakReference               wv;
				if (RegisteredInstances.TryGetValue (key, out wv)) {
					IJavaObject   t = (IJavaObject) wv.Target;
					if (t != null)
						return t;
					RegisteredInstances.Remove (key);
				}
			}
			return null;
		}
	}
}

