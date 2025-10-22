import { PrismaClient } from "./generated/prisma";

const prisma = new PrismaClient();

const customersData = [
  {
    name: 'ODEC CENTRO DE CALCULO Y APLICACIONES INFORMATICAS, S.A.',
    cif: 'A46063418',
    crmAccountId: '7d86adca-a3bf-e611-80ea-c4346badc0e4'
  },
  {
    name: '100M MONTADITOS INTERNACIONAL, S.L.',
    cif: 'B85777654',
    crmAccountId: '18f584d9-63c0-e611-80eb-c4346badc0e4'
  },
  {
    name: 'NTT DATA SPAIN, S.L.U.',
    cif: 'B82387770',
    crmAccountId: 'ee49e58f-a3bf-e611-80e9-c4346badd004'
  },
  {
    name: 'ATOS SPAIN, S.A.',
    cif: 'A28240752',
    crmAccountId: 'e0777e3b-abbf-e611-80e9-c4346bad6048'
  }
]

const membersData = [
  {
    fullName: 'Miguel Torres',
    email: 'miguel.torres@odec.com',
    ministryName: 'Torres, Miguel'
  },
  {
    fullName: 'Sofía Ruiz',
    email: 'sofía.ruiz@odec.com',
    ministryName: 'Ruiz, Sofía'
  },
  {
    fullName: 'Lucía Sánchez',
    email: 'lucía.sánchez@odec.com',
    ministryName: 'Sánchez, Lucía'
  },
  {
    fullName: 'Sofía López',
    email: 'sofía.lópez@odec.com',
    ministryName: 'López, Sofía'
  },
  {
    fullName: 'Marta Ramírez',
    email: 'marta.ramírez@odec.com',
    ministryName: 'Ramírez, Marta'
  },
  {
    fullName: 'Andrés Sánchez',
    email: 'andrés.sánchez@odec.com',
    ministryName: 'Sánchez, Andrés'
  },
  {
    fullName: 'Miguel Martínez',
    email: 'miguel.martínez@odec.com',
    ministryName: 'Martínez, Miguel'
  },
  {
    fullName: 'Elena Torres',
    email: 'elena.torres@odec.com',
    ministryName: 'Torres, Elena'
  },
  {
    fullName: 'Lucía Torres',
    email: 'lucía.torres@odec.com',
    ministryName: 'Torres, Lucía'
  },
  {
    fullName: 'Carlos Martínez',
    email: 'carlos.martínez@odec.com',
    ministryName: 'Martínez, Carlos'
  }
]

const servicesData = [
  {
    description: "2024 - 2025",
    fiscalYearStart: new Date('2024-04-01'),
    hasTrimestralEvidences: false,
  },
  {
    description: "2025 - 2026",
    fiscalYearStart: new Date('2025-04-01'),
    hasTrimestralEvidences: true,
  }
]

async function main() {
  console.log('🌱 Iniciando migración de datos a la base de datos...\n');

  // Limpiar datos existentes
  console.log('🗑️  Limpiando datos existentes...');
  await prisma.tracking.deleteMany();
  await prisma.exclusivePeriod.deleteMany();
  await prisma.resource.deleteMany();
  await prisma.service.deleteMany();
  await prisma.customer.deleteMany();
  await prisma.member.deleteMany();
  console.log('✅ Datos limpiados\n');

  // Insertar clientes
  console.log('🏢 Creando clientes...');
  for (const customerData of customersData) {
    const customer = await prisma.customer.create({
      data: customerData
    })
    console.log(`   ✓ ${customer.name}`);
  }
  console.log(`✅ Creados ${customersData.length} clientes\n`);

  console.log('📋 Creando servicios...');
  const customer = await prisma.customer.findFirst(
    { where: { name: 'ODEC CENTRO DE CALCULO Y APLICACIONES INFORMATICAS, S.A.' } }
  );
  if (!customer) {
    throw new Error('Customer not found');
  }
  for (const serviceData of servicesData) {
    const service = await prisma.service.create({
      data: {
        ...serviceData,
        customerId: customer.id,
      }
    })
    console.log(`   ✓ ${service.description}`);
    console.log(`      - Año fiscal: ${service.fiscalYearStart.toUTCString()}`);
    console.log(`      - Evidencias trimestrales: ${service.hasTrimestralEvidences ? 'Sí' : 'No'}`);
  }
  console.log(`✅ Creados ${servicesData.length} servicios\n`);

  console.log('👥 Creando miembros...');
  for (const memberData of membersData) {
    const member = await prisma.member.create({
      data: memberData
    })
    console.log(`   ✓ ${member.fullName}`);
  }
  console.log(`✅ Creados ${membersData.length} miembros\n`);

  console.log('💼 Creando investigadores...');
  const services = await prisma.service.findMany();
  const members = await prisma.member.findMany();

  for (const member of members) {
    for (const service of services) {
      const resource = await prisma.resource.create({
        data: {
          memberId: member.id,
          serviceId: service.id,
          proposalStatus: 'Approved'
        }
      });
      console.log(`   ✓ ${member.fullName} asignado al servicio ${service.description}`);
      console.log(`      - Estado de la propuesta: ${resource.proposalStatus}`);
      const exclusivePeriod = await prisma.exclusivePeriod.create({
        data: {
          resourceId: resource.id,
          number: 1,
          startDate: service.fiscalYearStart,
        }
      });
      console.log(`      - Período exclusivo creado desde ${exclusivePeriod.startDate.toUTCString()}`);
    }
  }

}

main()
  .then(async () => {
    await prisma.$disconnect();
  })
  .catch(async (e) => {
    console.error(e);
    await prisma.$disconnect();
    process.exit(1);
  });