namespace LostTech.TensorFlow {
    using System;
    using System.IO;
    using System.Linq;
    using NUnit.Framework;
    using Python.Runtime;

    public class Tests {
        [SetUp]
        public void Setup() {
        }

        [Test]
        public void EnsureCanGetTensorFlow() {
            var deploymentTarget = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TFP", Guid.NewGuid().ToString()));
            try {
                var environment = PackagedTensorFlow.EnsureDeployed(deploymentTarget);
                Runtime.PythonDLL = environment.DynamicLibraryPath.FullName;
                PythonEngine.PythonHome = environment.Home.FullName;
                PythonEngine.Initialize();

                using var _ = Py.GIL();
                dynamic tf = Py.Import("tensorflow");
                Console.WriteLine(tf.__version__);
            } finally {
                if (deploymentTarget.Exists)
                    deploymentTarget.Delete(recursive: true);
            }
        }
    }
}