using Acs1;
using AElf.Sdk.CSharp.State;

namespace AElf.Contracts.MultiToken
{
    public partial class TokenContractState
    {
        internal MappedState<string, MethodFees> MethodFees { get; set; }
        public Int64State TransactionFeeUnitPrice { get; set; }
        public MappedState<int, CalculateFeeCoefficientsOfType> CalculateCoefficient { get; set; }
        
    }
}