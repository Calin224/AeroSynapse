export default {
  test: {
    maxWorkers: 2,
    fileParallelism: false,
    reporters: [['junit', { outputFile: '/artifacts/results.xml' }], 'default'],
  },
};
