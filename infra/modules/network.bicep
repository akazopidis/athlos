// A virtual network for one Container Apps environment. VNets cost nothing; they are here because
// internal ingress was reachable from the internet in an environment without one (design §3.2).
param name string
param location string

@description('Address space, for example 10.10.0.0/16. Each environment gets its own.')
param addressPrefix string

@description('Subnet for the Container Apps environment (workload profiles need at least a /27).')
param acaSubnetPrefix string

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: name
  location: location
  properties: {
    addressSpace: { addressPrefixes: [addressPrefix] }
    subnets: [
      {
        name: 'snet-aca'
        properties: {
          addressPrefix: acaSubnetPrefix
          delegations: [
            { name: 'aca', properties: { serviceName: 'Microsoft.App/environments' } }
          ]
        }
      }
    ]
  }
}

output acaSubnetId string = vnet.properties.subnets[0].id
