using System;
using AElf.CSharp.Core.Utils;
using AElf.Types;
using Shouldly;
using Xunit;

namespace AElf.CSharp.Core
{
    public class PreconditionsTests : TypesCSharpTestBase
    {
        [Fact]
        public void PreCondition_Check_Test()
        {
            Func<Address> func1 = null;
            Should.Throw<ArgumentException>(() => Preconditions.CheckNotNull(func1));
            
            func1 = () => Address.FromBase58("z1NVbziJbekvcza3Zr4Gt4eAvoPBZThB68LHRQftrVFwjtGVM");
            var reference = Preconditions.CheckNotNull(func1);
            reference.ShouldNotBeNull();
            var addressInfo = reference();
            addressInfo.ShouldNotBeNull();
            addressInfo.GetType().ToString().ShouldBe("AElf.Types.Address");

            Func<Address, string> func2 = address => address.ToBase58();
            var reference1 = Preconditions.CheckNotNull(func2, "address");
            
            reference1.ShouldNotBeNull();
            var result = reference1(Address.FromBase58("z1NVbziJbekvcza3Zr4Gt4eAvoPBZThB68LHRQftrVFwjtGVM"));
            result.ShouldNotBeNullOrEmpty();
        }
    }
}